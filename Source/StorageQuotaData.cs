using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace StorageGroupQuotas
{
    public enum QuotaMode
    {
        TotalCount,
        SimilarStacks
    }

    public sealed class StorageQuotaData : IExposable
    {
        private int defaultUpper;
        private QuotaMode quotaMode;
        private int similarStackCount = 2;
        private Dictionary<string, int> upperByDefName = new Dictionary<string, int>();

        public QuotaMode Mode
        {
            get => quotaMode;
            set => quotaMode = value;
        }

        public int SimilarStackCount
        {
            get => similarStackCount;
            set => similarStackCount = Math.Max(1, value);
        }

        public int DefaultUpper
        {
            get => defaultUpper;
            set => defaultUpper = Math.Max(0, value);
        }

        public bool Active => quotaMode == QuotaMode.SimilarStacks
            || defaultUpper > 0
            || upperByDefName.Count > 0;

        public IEnumerable<string> OverriddenDefNames => upperByDefName.Keys;

        public int EffectiveValue(ThingDef def)
        {
            if (def != null && upperByDefName.TryGetValue(def.defName, out int upper))
            {
                return upper;
            }

            return defaultUpper;
        }

        public int EffectivePerStackUpper(ThingDef def)
        {
            if (quotaMode != QuotaMode.SimilarStacks || def == null)
            {
                return int.MaxValue;
            }

            int nativeLimit = Math.Max(1, def.stackLimit);
            int configured = EffectiveValue(def);
            return configured == 0 ? nativeLimit : Math.Min(configured, nativeLimit);
        }

        public int EffectiveTotalUpper(ThingDef def)
        {
            int configured = EffectiveValue(def);
            if (quotaMode == QuotaMode.TotalCount)
            {
                return configured == 0 ? int.MaxValue : configured;
            }

            long total = (long)EffectivePerStackUpper(def) * similarStackCount;
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        public bool HasOverride(ThingDef def)
        {
            return def != null && upperByDefName.ContainsKey(def.defName);
        }

        public int GetOverride(ThingDef def)
        {
            return def != null && upperByDefName.TryGetValue(def.defName, out int value) ? value : 0;
        }

        public void SetOverride(ThingDef def, int upper)
        {
            if (def == null)
            {
                return;
            }

            upperByDefName[def.defName] = Math.Max(0, upper);
        }

        public void RemoveOverride(ThingDef def)
        {
            if (def != null)
            {
                upperByDefName.Remove(def.defName);
            }
        }

        public StorageQuotaData Clone()
        {
            return new StorageQuotaData
            {
                defaultUpper = defaultUpper,
                quotaMode = quotaMode,
                similarStackCount = similarStackCount,
                upperByDefName = new Dictionary<string, int>(upperByDefName)
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref defaultUpper, "defaultUpper", 0);
            Scribe_Values.Look(ref quotaMode, "quotaMode", QuotaMode.TotalCount);
            Scribe_Values.Look(ref similarStackCount, "similarStackCount", 2);
            Scribe_Collections.Look(ref upperByDefName, "upperByDefName", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && upperByDefName == null)
            {
                upperByDefName = new Dictionary<string, int>();
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                similarStackCount = Math.Max(1, similarStackCount);
            }
        }
    }
}
