﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Raven.Client.Documents.Replication.Messages;
using Sparrow.Json.Parsing;

namespace Raven.Server.Extensions
{
    public static class ChangeVectorExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dictionary<Guid, long> ToDictionary(this ChangeVectorEntry[] changeVector)
        {
            return changeVector.ToDictionary(x => x.DbId, x => x.Etag);
        }

        public static DynamicJsonArray ToJson(this ChangeVectorEntry[] self)
        {
            var results = new DynamicJsonArray();
            foreach (var entry in self)
                results.Add(entry.ToJson());
            return results;
        }

        public static bool GreaterThan(this ChangeVectorEntry[] self, Dictionary<Guid, long> other)
        {
            for (int i = 0; i < self.Length; i++)
            {
                long otherEtag;
                if (other.TryGetValue(self[i].DbId, out otherEtag) == false)
                    return true;
                if (self[i].Etag > otherEtag)
                    return true;
            }
            return false;
        }

        public static bool GreaterThan(this ChangeVectorEntry[] self, ChangeVectorEntry[] other)
        {
            for (int i = 0; i < self.Length; i++)
            {
                var indexOfDbId = IndexOf(self[i].DbId, other);
                if (indexOfDbId == -1)
                    return true;
                if (self[i].Etag > other[indexOfDbId].Etag)
                    return true;
            }
            return false;
        }

        private static int IndexOf(Guid DbId, ChangeVectorEntry[] v)
        {
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i].DbId == DbId)
                    return i;
            }
            return -1;
        }

        public static string Format(this ChangeVectorEntry[] changeVector, int? maxCount = null)
        {
            var max = maxCount ?? changeVector.Length;
            if (max == 0)
                return "[]";
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < max; i++)
            {
                sb.Append(changeVector[i].DbId)
                    .Append(" : ")
                    .Append(changeVector[i].Etag)
                    .Append(", ");
            }
            sb.Length -= 2;
            sb.Append("]");
            return sb.ToString();
        }
    }
}
