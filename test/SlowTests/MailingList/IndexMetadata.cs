using System.Linq;
using System.Threading.Tasks;
using FastTests;
using FastTests.Server.Basic.Entities;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.MailingList
{
    public class IndexMetadata : RavenNewTestBase
    {
        private class Users_DeleteStatus : AbstractMultiMapIndexCreationTask
        {
            public Users_DeleteStatus()
            {
                AddMap<User>(users => from user in users
                                      select new
                                      {
                                          Deleted = MetadataFor(user)["Deleted"]
                                      });
            }
        }

        [Fact]
        public void WillGenerateProperIndex()
        {
            var usersDeleteStatus = new Users_DeleteStatus { Conventions = new DocumentConventions() };
            var indexDefinition = usersDeleteStatus.CreateIndexDefinition();
            Assert.Contains("Deleted = this.MetadataFor(user)[\"Deleted\"]", indexDefinition.Maps.First());
        }

        [Fact]
        public void CanCreateIndex()
        {
            using (var store = GetDocumentStore())
            {
                new Users_DeleteStatus().Execute(store);
            }
        }
    }
}
