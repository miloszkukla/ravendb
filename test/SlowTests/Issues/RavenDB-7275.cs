﻿using System;
using System.Threading.Tasks;
using FastTests;
using FastTests.Client.Attachments;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Security;
using Raven.Client.Server.Operations.ApiKeys;
using Raven.Client.Util;
using Raven.Server.Config.Attributes;
using Raven.Tests.Core.Utils.Entities;
using SlowTests.Server.Documents.Notifications;
using Sparrow;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_7275 : RavenTestBase
    {
        private readonly ApiKeyDefinition _apiKey = new ApiKeyDefinition
        {
            Enabled = true,
            Secret = "secret",
            ResourcesAccessMode =
            {
                ["db/CanGetDocWithValidToken"] = AccessMode.ReadWrite,
                ["db/CanGetTokenFromServer"] = AccessMode.Admin
            }
        };


        [Fact]
        public async Task ValidateSubscriptionAuthorizationRejectOnCreationAsync()
        {
            DoNotReuseServer();
            Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
            AccessMode[] modes = { AccessMode.None, AccessMode.ReadOnly };
            using (var store = GetDocumentStore(apiKey: "super/" + _apiKey.Secret))
            {
                foreach (var accessMode in modes)
                {
                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
                    _apiKey.ResourcesAccessMode[store.Database] = accessMode;

                    store.Admin.Server.Send(new PutApiKeyOperation("super", _apiKey));
                    var doc = store.Admin.Server.Send(new GetApiKeyOperation("super"));
                    Assert.NotNull(doc);

                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.None;

                    await Assert.ThrowsAsync<AuthorizationException>(async () => await store.Subscriptions.CreateAsync(new SubscriptionCreationOptions<User>()));
                }
            }
        }

        [Fact]
        public async Task ValidateSubscriptionAuthorizationAcceptOnCreation()
        {
            DoNotReuseServer();
            Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
            AccessMode[] modes = { AccessMode.ReadWrite, AccessMode.Admin };
            using (var store = GetDocumentStore(apiKey: "super/" + _apiKey.Secret))
            {
                foreach (var accessMode in modes)
                {
                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
                    _apiKey.ResourcesAccessMode[store.Database] = accessMode;

                    store.Admin.Server.Send(new PutApiKeyOperation("super", _apiKey));
                    var doc = store.Admin.Server.Send(new GetApiKeyOperation("super"));
                    Assert.NotNull(doc);

                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.None;

                    var subscriptionId = await store.Subscriptions.CreateAsync(new SubscriptionCreationOptions<User>());

                    var subscription = store.Subscriptions.Open<User>(new SubscriptionConnectionOptions(subscriptionId)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromMilliseconds(200)
                    });

                    var mre = new AsyncManualResetEvent();
                    

                    subscription.AfterAcknowledgment += b => { mre.Set(); return Task.CompletedTask; };

                    GC.KeepAlive(subscription.Run(x => { }));

                    await mre.WaitAsync(TimeSpan.FromSeconds(20));
                }
            }
        }

        [Fact]
        public async Task ValidateSubscriptionAuthorizationRejectOnOpening()
        {
            DoNotReuseServer();
            Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
            AccessMode[] modes = {AccessMode.None, AccessMode.ReadOnly};
            foreach (var accessMode in modes)
            {
                using (var store = GetDocumentStore(apiKey: "super/" + _apiKey.Secret))
                {
                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
                    _apiKey.ResourcesAccessMode[store.Database] = accessMode;

                    var subscriptionId = await store.Subscriptions.CreateAsync(
                        new SubscriptionCreationOptions<User>());

                    store.Admin.Server.Send(new PutApiKeyOperation("super", _apiKey));
                    var doc = store.Admin.Server.Send(new GetApiKeyOperation("super"));
                    Assert.NotNull(doc);

                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.None;
                    var subscription = store.Subscriptions.Open<User>(new SubscriptionConnectionOptions(subscriptionId)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromMilliseconds(200)
                    });
                    await Assert.ThrowsAsync<AuthorizationException>(async () => await subscription.Run(user => { }));
                }
            }
        }

        [Fact]
        public async Task ValidateSubscriptionAuthorizationAcceptOnOpening()
        {
            DoNotReuseServer();
            Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
            AccessMode[] modes = { AccessMode.ReadWrite, AccessMode.Admin };
            using (var store = GetDocumentStore(apiKey: "super/" + _apiKey.Secret))
            {
                foreach (var accessMode in modes)
                {
                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.Admin;
                    _apiKey.ResourcesAccessMode[store.Database] = accessMode;

                    var subscriptionId = await store.Subscriptions.CreateAsync(
                        new SubscriptionCreationOptions<User>());

                    store.Admin.Server.Send(new PutApiKeyOperation("super", _apiKey));
                    var doc = store.Admin.Server.Send(new GetApiKeyOperation("super"));
                    Assert.NotNull(doc);

                    Server.Configuration.Server.AnonymousUserAccessMode = AnonymousUserAccessModeValues.None;

                    var subscription = store.Subscriptions.Open<User>(new SubscriptionConnectionOptions(subscriptionId)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromMilliseconds(200)
                    });

                    var mre = new AsyncManualResetEvent();

                    subscription.AfterAcknowledgment += b => { mre.Set(); return Task.CompletedTask; };

                    GC.KeepAlive(subscription.Run(x => { }));

                    await mre.WaitAsync(TimeSpan.FromSeconds(20));
                }
            }
        }
    }
}
