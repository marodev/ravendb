using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Server;
using Raven.Client.Server.Operations;
using Raven.Server.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests.Tasks
{
    public class RavenDB_7059 : ClusterTestBase
    {
        private readonly string _fileName = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.ravendump");

        public class User
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }
        
        [Fact]
        public async Task Cluster_identity_should_work_with_smuggler()
        {
            const int clusterSize = 3;
            const string databaseName = "Cluster_identity_for_single_document_should_work";
            var leaderServer = await CreateRaftClusterAndGetLeader(clusterSize);
            using (var leaderStore = new DocumentStore
            {
                Urls = leaderServer.WebUrls,
                Database = databaseName
            })
            {
                leaderStore.Initialize();
                
                await CreateDatabasesInCluster(clusterSize, databaseName, leaderStore);
                using (var session = leaderStore.OpenSession())
                {
                    session.Store(new User { Name = "John Dow" }, "users/");
                    session.Store(new User { Name = "Jake Dow" }, "users/");
                    session.SaveChanges();                   
                }
                
                await leaderStore.Smuggler.ExportAsync(new DatabaseSmugglerOptions
                {
                    Database = databaseName,
                    FileName = _fileName
                }, _fileName);
            }

            foreach (var server in Servers)
            {
                server.Dispose();
            }
            Servers.Clear();

            leaderServer = await CreateRaftClusterAndGetLeader(clusterSize);
            using (var leaderStore = new DocumentStore
            {
                Urls = leaderServer.WebUrls,
                Database = databaseName
            })
            {
                leaderStore.Initialize();
                
                await CreateDatabasesInCluster(clusterSize, databaseName, leaderStore);
                await leaderStore.Smuggler.ImportAsync(new DatabaseSmugglerOptions
                {
                    Database = databaseName,
                    FileName = _fileName
                }, _fileName);
                
                using (var session = leaderStore.OpenSession())
                {
                    session.Store(new User { Name = "Julie Dow" }, "users/");
                    session.SaveChanges();                   
                }

                using (var session = leaderStore.OpenSession())
                {
                    var julieDow = session.Query<User>().First(u => u.Name.StartsWith("Julie"));
                    Assert.Equal("users/3",julieDow.Id);

                }
            }                        
        }
        
        private async Task CreateDatabasesInCluster(int clusterSize, string databaseName, IDocumentStore store)
        {
            var databaseResult = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(MultiDatabase.CreateDatabaseDocument(databaseName), clusterSize));
            Assert.Equal(clusterSize, databaseResult.Topology.AllReplicationNodes().Count());
            foreach (var server in Servers)
            {
                await server.ServerStore.Cluster.WaitForIndexNotification(databaseResult.ETag);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            IOExtensions.DeleteFile(_fileName);
        }
    }
}