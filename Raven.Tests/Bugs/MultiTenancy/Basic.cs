//-----------------------------------------------------------------------
// <copyright file="Basic.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Client.Document;
using Raven.Client.Extensions;
using Raven.Database.Config;
using Raven.Database.Extensions;
using Raven.Database.Server;
using Raven.Server;
using Xunit;

namespace Raven.Tests.Bugs.MultiTenancy
{
	public class Basic : RemoteClientTest, IDisposable
	{
		protected RavenDbServer GetNewServer(int port)
		{
			return new RavenDbServer(new RavenConfiguration
				{
					Port = port,
					RunInMemory = true,
					DataDirectory = "Data",
					AnonymousUserAccessMode = AnonymousUserAccessMode.Admin
				});
		}

		[Fact]
		public void CanCreateDatabaseUsingExtensionMethod()
		{
			using (GetNewServer(8079))
			using (var store = new DocumentStore
			{
				Url = "http://localhost:8079"
			}.Initialize())
			{
				store.DatabaseCommands.EnsureDatabaseExists("Northwind");
				
				string userId;

				using (var s = store.OpenSession("Northwind"))
				{
					var entity = new User
					{
						Name = "First Multitenant Bank",
					};
					s.Store(entity);
					userId = entity.Id;
					s.SaveChanges();
				}

				using (var s = store.OpenSession())
				{
					Assert.Null(s.Load<User>(userId));
				}

				using (var s = store.OpenSession("Northwind"))
				{
					Assert.NotNull(s.Load<User>(userId));
				}
			}
		}

		[Fact]
		public void CanQueryTenantDatabase()
		{
			using (GetNewServer(8079))
			using (var store = new DocumentStore
			{
				Url = "http://localhost:8079"
			}.Initialize())
			{
				store.DatabaseCommands.EnsureDatabaseExists("Northwind");

				using (var s = store.OpenSession("Northwind"))
				{
					var entity = new User
					{
						Name = "Hello",
					};
					s.Store(entity);
					s.SaveChanges();
				}

				using (var s = store.OpenSession("Northwind"))
				{
					Assert.NotEmpty(s.Query<User>().Where(x => x.Name == "Hello"));
				}
			}
		}


		[Fact]
		public void CanQueryDefaultDatabaseQuickly()
		{
			using (GetNewServer(8079))
			using (var store = new DocumentStore
			{
				Url = "http://localhost:8079"
			}.Initialize())
			{
				store.DatabaseCommands.EnsureDatabaseExists("Northwind");

				using (var s = store.OpenSession("Northwind"))
				{
					var entity = new User
					{
						Name = "Hello",
					};
					s.Store(entity);
					s.SaveChanges();
				}

				var sp = Stopwatch.StartNew();
				using (var s = store.OpenSession())
				{
					Assert.Empty(s.Query<User>().Where(x => x.Name == "Hello"));
				}
				Assert.True(TimeSpan.FromSeconds(5) > sp.Elapsed);
			}
		}


		[Fact]
		public void OpenSessionUsesSpecifiedDefaultDatabase()
		{
			using (GetNewServer(8079))
			using (var store = new DocumentStore
			{
				Url = "http://localhost:8079",
				DefaultDatabase = "Northwind"
			}.Initialize())
			{
				store.DatabaseCommands.EnsureDatabaseExists("Northwind");

				string userId;

				using (var s = store.OpenSession("Northwind"))
				{
					var entity = new User
					{
						Name = "First Multitenant Bank",
					};
					s.Store(entity);
					userId = entity.Id;
					s.SaveChanges();
				}

				using (var s = store.OpenSession())
				{
					Assert.NotNull(s.Load<User>(userId));
				}
			}
		}

		[Fact]
		public void CanUseMultipleDatabases()
		{
			using(GetNewServer(8079))
			using(var store = new DocumentStore
			{
				Url = "http://localhost:8079"
			}.Initialize())
			{
				using(var session = store.OpenSession())
				{
					session.Store(new DatabaseDocument
					{
						Id = "Raven/Databases/Northwind",
						Settings =
							{
								{"Raven/RunInMemory", "true"},
								{"Raven/DataDir", "Northwind"}
							}
					});

					session.SaveChanges();
				}

				string userId;

				using(var session = store.OpenSession("Northwind"))
				{
					var entity = new User
					{
						Name = "First Multitenant Bank",
					};
					session.Store(entity);
					userId = entity.Id;
					session.SaveChanges();
				}

				using (var session = store.OpenSession())
				{
					Assert.Null(session.Load<User>(userId));
				}

				using (var session = store.OpenSession("Northwind"))
				{
					Assert.NotNull(session.Load<User>(userId));
				}
			}
		}

		[Fact]
		public void RavenDocumentsByEntityNameIndexCreated()
		{
			using (GetNewServer(8079))
			using (var store = new DocumentStore
			{
				Url = "http://localhost:8079"
			}.Initialize())
			{
				store.DatabaseCommands.EnsureDatabaseExists("Northwind");
				var index = store.DatabaseCommands.ForDatabase("Northwind").GetIndex("Raven/DocumentsByEntityName");
				Assert.NotNull(index);
			}
		}

		[Fact]
		public async Task RavenDocumentsByEntityNameIndexCreatedAsync()
		{
			using (GetNewServer(8079))
			using (var store = new DocumentStore
			{
				Url = "http://localhost:8079"
			}.Initialize())
			{
				await store.AsyncDatabaseCommands.EnsureDatabaseExistsAsync("Northwind");
				var index = await store.AsyncDatabaseCommands.ForDatabase("Northwind").GetIndexAsync("Raven/DocumentsByEntityName");
				Assert.NotNull(index);
			}
		}

		public override void Dispose()
		{
			IOExtensions.DeleteDirectory("Data");
			IOExtensions.DeleteDirectory("NHibernate");
			base.Dispose();
		}
	}
}