﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FastTests;
using Orders;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Smuggler;
using Raven.Server.Config;
using Raven.Server.Utils.Enumerators;
using SlowTests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_13972_32_bits : RavenTestBase
    {
        [Theory]
        [InlineData(2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 2, 2, 2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 2)]
        [InlineData(2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 2, 2, 0, 2)]
        [InlineData(2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 2, 2, 2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 3)]
        [InlineData(4 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 3, 2, 2, 0, 3)]
        [InlineData(4 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 3, 2, 2, 4 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 3, 2)]
        public async Task CanExportWithPulsatingReadTransaction(int numberOfUsers, int numberOfCountersPerUser, int numberOfRevisionsPerDocument, int numberOfOrders, int deleteUserFactor)
        {
            var file = GetTempFileName();
            var fileAfterDeletions = GetTempFileName();

            using (var server = GetNewServer(new ServerCreationOptions
            {
                CustomSettings = new Dictionary<string, string>
                {
                    [RavenConfiguration.GetKey(x => x.Storage.ForceUsing32BitsPager)] = "true"

                }
            }))
            using (var storeToExport = GetDocumentStore(new Options
            {
                Server = server,
                ModifyDatabaseRecord = record =>
                {
                    record.Settings[RavenConfiguration.GetKey(x => x.Databases.PulseReadTransactionLimit)] = "0";
                }
            }))
            using (var storeToImport = GetDocumentStore(new Options
            {
                Server = server
            }))
            using (var storeToAfterDeletions = GetDocumentStore(new Options
            {
                Server = server
            }))
            {
                if (numberOfRevisionsPerDocument > 0)
                {
                    var configuration = new RevisionsConfiguration
                    {
                        Default = new RevisionsCollectionConfiguration
                        {
                            Disabled = false,
                            MinimumRevisionsToKeep = 10
                        }
                    };

                    await storeToExport.Maintenance.SendAsync(new ConfigureRevisionsOperation(configuration));
                }

                using (var bulk = storeToExport.BulkInsert())
                {
                    for (int i = 0; i < Math.Max(numberOfUsers, numberOfOrders); i++)
                    {
                        if (i < numberOfUsers)
                            bulk.Store(new User(), "users/" + i);

                        if (i < numberOfOrders)
                            bulk.Store(new Order(), "orders/" + i);
                    }
                }

                if (numberOfRevisionsPerDocument > 2)
                {
                    for (int j = 0; j < numberOfRevisionsPerDocument; j++)
                    {
                        using (var bulk = storeToExport.BulkInsert())
                        {
                            for (int i = 0; i < Math.Max(numberOfUsers, numberOfOrders); i++)
                            {
                                if (i < numberOfUsers)
                                {
                                    bulk.Store(new User() {Name = i + " " + j}, "users/" + i);
                                }

                                if (i < numberOfOrders)
                                {
                                    bulk.Store(new Order() { Company = i + " " + j }, "orders/" + i);
                                }
                            }
                        }
                    }
                }

                using (var session = storeToExport.OpenSession())
                {
                    for (int i = 0; i < numberOfUsers; i++)
                    {
                        for (int j = 0; j < numberOfCountersPerUser; j++)
                        {
                            session.CountersFor("users/" + i).Increment("counter/" + j, 100);
                        }
                    }

                    session.SaveChanges();
                }

                var originalStats = await storeToExport.Maintenance.SendAsync(new GetStatisticsOperation());

                var options = new DatabaseSmugglerExportOptions();

                var operation = await storeToExport.Smuggler.ExportAsync(options, file);
                var result = await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(2));

                SmugglerResult.SmugglerProgress progress = ((SmugglerResult)result).Progress as SmugglerResult.SmugglerProgress;

                Assert.Equal(originalStats.CountOfDocuments, progress.Documents.ReadCount);
                Assert.Equal(originalStats.CountOfCounterEntries, progress.Counters.ReadCount);
                Assert.Equal(originalStats.CountOfRevisionDocuments, progress.RevisionDocuments.ReadCount);

                operation = await storeToImport.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);
                await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(2));

                var stats = await storeToImport.Maintenance.SendAsync(new GetStatisticsOperation());

                Assert.Equal(numberOfUsers + numberOfOrders, stats.CountOfDocuments);
                Assert.Equal(numberOfUsers, stats.CountOfCounterEntries);

                var expectedNumberOfRevisions = (numberOfUsers + numberOfOrders) * numberOfRevisionsPerDocument;

                if (numberOfCountersPerUser > 0)
                {
                    // if we added counters then additional revisions were created
                    expectedNumberOfRevisions += numberOfUsers;
                }

                Assert.Equal(expectedNumberOfRevisions, stats.CountOfRevisionDocuments);

                // deleting some docs

                var deletedUsers = 0;

                using (var session = storeToExport.OpenSession())
                {
                    for (int i = 0; i < numberOfUsers; i++)
                    {
                        if (i % deleteUserFactor != 0)
                            continue;

                        session.Delete("users/" + i);

                        deletedUsers++;
                    }

                    session.SaveChanges();
                }

                // import to new db

                var originalStatsAfterDeletions = await storeToExport.Maintenance.SendAsync(new GetStatisticsOperation());

               // TODO arek options.OperateOnTypes |= DatabaseItemType.Tombstones;

                operation = await storeToExport.Smuggler.ExportAsync(options, fileAfterDeletions);
                result = await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(2));

                progress = ((SmugglerResult)result).Progress as SmugglerResult.SmugglerProgress;

                Assert.Equal(originalStatsAfterDeletions.CountOfDocuments, progress.Documents.ReadCount);
                Assert.Equal(originalStatsAfterDeletions.CountOfCounterEntries, progress.Counters.ReadCount);
                Assert.Equal(originalStatsAfterDeletions.CountOfRevisionDocuments, progress.RevisionDocuments.ReadCount);
                // TODO arek Assert.Equal(originalStats.CountOfTombstones, progress.Tombstones.ReadCount);

                var importOptions = new DatabaseSmugglerImportOptions();

                importOptions.OperateOnTypes |= DatabaseItemType.Tombstones;

                operation = await storeToAfterDeletions.Smuggler.ImportAsync(importOptions, fileAfterDeletions);
                await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(2));

                var statsAfterDeletions = await storeToAfterDeletions.Maintenance.SendAsync(new GetStatisticsOperation());

                Assert.Equal(numberOfUsers - deletedUsers + numberOfOrders, statsAfterDeletions.CountOfDocuments);
                Assert.Equal(numberOfUsers - deletedUsers, statsAfterDeletions.CountOfCounterEntries);
                Assert.Equal(expectedNumberOfRevisions, statsAfterDeletions.CountOfRevisionDocuments);
                // TODO arek Assert.Equal(deletedUsers, statsAfterDeletions.CountOfTombstones);
            }
        }


        [Theory]
        [InlineData(2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 0, 2)]
        [InlineData(2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 2 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 10, 3)]
        [InlineData(4 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 3, 0, 3)]
        [InlineData(4 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 3, 4 * PulsedEnumerationState<object>.NumberOfEnumeratedDocumentsToCheckIfPulseLimitExceeded + 3, 2)]
        public void CanStreamDocumentsWithPulsatingReadTransaction(int numberOfUsers, int numberOfOrders, int deleteUserFactor)
        {
            using (var server = GetNewServer(new ServerCreationOptions
            {
                CustomSettings = new Dictionary<string, string>
                {
                    [RavenConfiguration.GetKey(x => x.Storage.ForceUsing32BitsPager)] = "true"

                }
            }))
            using (var store = GetDocumentStore(new Options
            {
                Server = server,
                ModifyDatabaseRecord = record =>
                {
                    record.Settings[RavenConfiguration.GetKey(x => x.Databases.PulseReadTransactionLimit)] = "0";
                }
            }))
            {
                using (var bulk = store.BulkInsert())
                {
                    for (int i = 0; i < Math.Max(numberOfUsers, numberOfOrders); i++)
                    {
                        if (i < numberOfUsers)
                            bulk.Store(new User(), "users/" + i);

                        if (i < numberOfOrders)
                            bulk.Store(new Order(), "orders/" + i);
                    }
                }

                AssertAllDocsStreamed(store, numberOfUsers, numberOfOrders);

                AssertAllDocsStreamedWithPaging(store, numberOfUsers, numberOfOrders);

                AssertAllStartsWithDocsStreamed(store, numberOfUsers);

                AssertAllStartsWithDocsStreamedWithPaging(store, numberOfUsers);

                AssertAllStartAfterDocsStreamed(store, numberOfUsers);

                AssertAllMatchesDocsStreamed(store, numberOfUsers);

                AssertAllMatchesDocsStreamedWithPaging(store, numberOfUsers);

                // deleting some docs

                var deletedUsers = 0;

                using (var session = store.OpenSession())
                {
                    for (int i = 0; i < numberOfUsers; i++)
                    {
                        if (i % deleteUserFactor != 0)
                            continue;

                        session.Delete("users/" + i);

                        deletedUsers++;
                    }

                    session.SaveChanges();
                }

                AssertAllDocsStreamed(store, numberOfUsers - deletedUsers, numberOfOrders);

                AssertAllDocsStreamedWithPaging(store, numberOfUsers - deletedUsers, numberOfOrders);

                AssertAllStartsWithDocsStreamed(store, numberOfUsers - deletedUsers);

                AssertAllStartsWithDocsStreamedWithPaging(store, numberOfUsers - deletedUsers);

                AssertAllStartAfterDocsStreamed(store, numberOfUsers - deletedUsers);

                AssertAllMatchesDocsStreamed(store, numberOfUsers - deletedUsers);

                AssertAllMatchesDocsStreamed(store, numberOfUsers);
                
                AssertAllMatchesDocsStreamedWithPaging(store, numberOfUsers);
            }
        }

        private static void AssertAllStartsWithDocsStreamedWithPaging(DocumentStore store, int numberOfUsers)
        {
            using (var session = store.OpenSession())
            {
                var start = 10;

                var en = session.Advanced.Stream<User>("users/", start: start);

                var count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(numberOfUsers - start, count);

                var take = numberOfUsers / 2 + 3;

                en = session.Advanced.Stream<User>("users/", start: start, pageSize: take);

                count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(take, count);
            }
        }

        private static void AssertAllStartsWithDocsStreamed(DocumentStore store, int numberOfUsers)
        {
            using (var session = store.OpenSession())
            {
                var en = session.Advanced.Stream<User>("users/");

                var count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(numberOfUsers, count);
            }
        }

        private static void AssertAllStartAfterDocsStreamed(DocumentStore store, int numberOfUsers)
        {
            var ids = new HashSet<string>();

            using (var session = store.OpenSession())
            {
                var en = session.Advanced.Stream<User>("users/", startAfter: "users/2");

                var count = 0;

                while (en.MoveNext())
                {
                    var added = ids.Add(en.Current.Id);

                    Assert.True(added, "Duplicated Id: " + en.Current.Id);

                    Assert.True(en.Current.Id.CompareTo("users/2") >= 0, "Found Id that isn't greater that startsAfter parameter: " + en.Current.Id);

                    count++;
                }

                Assert.True(count > 0, "count > 0");
                Assert.True(count < numberOfUsers, "count < numberOfUsers");
            }
        }

        private static void AssertAllMatchesDocsStreamed(DocumentStore store, int numberOfUsers)
        {
            var ids = new HashSet<string>();

            using (var session = store.OpenSession())
            {
                var en = session.Advanced.Stream<User>("u", matches: "*ers/2*");

                var count = 0;

                while (en.MoveNext())
                {
                    var added = ids.Add(en.Current.Id);

                    Assert.True(added, "Duplicated Id: " + en.Current.Id);

                    Assert.True(en.Current.Id.StartsWith("users/2"), "Found Id that doesn't start with 'users/2': " + en.Current.Id);

                    count++;
                }

                Assert.True(count > 0, "count > 0");
                Assert.True(count < numberOfUsers, "count < numberOfUsers");
            }
        }

        private static void AssertAllMatchesDocsStreamedWithPaging(DocumentStore store, int numberOfUsers)
        {
            var ids = new HashSet<string>();

            using (var session = store.OpenSession())
            {
                var matches = "*ers/2*";

                var en = session.Advanced.Stream<User>("u", matches: matches);

                var numberOfResults = 0;

                while (en.MoveNext())
                {
                    var added = ids.Add(en.Current.Id);

                    Assert.True(added, "Duplicated Id: " + en.Current.Id);

                    Assert.True(en.Current.Id.StartsWith("users/2"), "Found Id that doesn't start with 'users/2': " + en.Current.Id);

                    numberOfResults++;
                }

                Assert.True(numberOfResults > 0, "numberOfResults > 0");
                Assert.True(numberOfResults < numberOfUsers, "numberOfResults < numberOfUsers");

                var start = 10;

                en = session.Advanced.Stream<User>("u", start: start, matches: matches);

                int count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(numberOfResults - start, count);

                var take = numberOfResults / 2 + 3;

                en = session.Advanced.Stream<User>("u", start: start, matches: matches, pageSize: take);

                count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(take, count);
            }
        }

        private static void AssertAllDocsStreamedWithPaging(DocumentStore store, int numberOfUsers, int numberOfOrders)
        {
            using (var session = store.OpenSession())
            {
                var start = 11;

                var en = session.Advanced.Stream<User>((string)null, start: start);

                var count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(numberOfUsers + numberOfOrders - start, count);

                var take = numberOfUsers / 2 + 3;

                en = session.Advanced.Stream<User>((string)null, start: start, pageSize: take);

                count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(take, count);
            }
        }

        private static void AssertAllDocsStreamed(DocumentStore store, int numberOfUsers, int numberOfOrders)
        {
            using (var session = store.OpenSession())
            {
                var en = session.Advanced.Stream<User>((string)null);

                var count = 0;

                while (en.MoveNext())
                {
                    count++;
                }

                Assert.Equal(numberOfUsers + numberOfOrders, count);
            }
        }
    }
}