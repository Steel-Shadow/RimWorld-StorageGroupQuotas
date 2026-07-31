using System.Runtime.CompilerServices;
using RimWorld;

namespace StorageGroupQuotas
{
    internal static class QuotaDataStore
    {
        private sealed class Holder
        {
            internal StorageQuotaData Data;
        }

        private static readonly ConditionalWeakTable<StorageSettings, Holder> DataBySettings =
            new ConditionalWeakTable<StorageSettings, Holder>();

        internal static StorageQuotaData Get(StorageSettings settings)
        {
            return DataBySettings.GetValue(settings, _ => new Holder { Data = new StorageQuotaData() }).Data;
        }

        internal static bool TryGet(StorageSettings settings, out StorageQuotaData data)
        {
            if (settings != null && DataBySettings.TryGetValue(settings, out Holder holder))
            {
                data = holder.Data;
                return data != null;
            }

            data = null;
            return false;
        }

        internal static void Set(StorageSettings settings, StorageQuotaData data)
        {
            if (settings == null)
            {
                return;
            }

            DataBySettings.Remove(settings);
            if (data != null)
            {
                DataBySettings.Add(settings, new Holder { Data = data });
            }
        }
    }
}
