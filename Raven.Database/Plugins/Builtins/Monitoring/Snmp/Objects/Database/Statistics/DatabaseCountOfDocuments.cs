﻿// -----------------------------------------------------------------------
//  <copyright file="DatabaseCountOfDocuments.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using Lextm.SharpSnmpLib;

using Raven.Database.Server.Tenancy;

namespace Raven.Database.Plugins.Builtins.Monitoring.Snmp.Objects.Database.Statistics
{
	public class DatabaseCountOfDocuments : DatabaseScalarObjectBase
	{
		public DatabaseCountOfDocuments(string databaseName, DatabasesLandlord landlord, int index)
			: base(databaseName, landlord, "5.2.{0}.1.6", index)
		{
		}

		protected override ISnmpData GetData(DocumentDatabase database)
		{
			return new Gauge32(GetCount(database));
		}

		private static long GetCount(DocumentDatabase database)
		{
			var count = 0L;
			database.TransactionalStorage.Batch(actions =>
			{
				count = actions.Documents.GetDocumentsCount();
			});

			return count;
		}
	}
}