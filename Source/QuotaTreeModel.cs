using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace StorageGroupQuotas
{
    internal sealed class QuotaTreeModel
    {
        internal sealed class Row
        {
            internal ThingCategoryDef Category;
            internal ThingDef Thing;
            internal int Depth;
            internal bool HasChildren;

            internal bool IsCategory => Category != null;
        }

        private readonly HashSet<ThingCategoryDef> includedCategories =
            new HashSet<ThingCategoryDef>();
        private readonly Dictionary<ThingCategoryDef, List<ThingCategoryDef>> childCategories =
            new Dictionary<ThingCategoryDef, List<ThingCategoryDef>>();
        private readonly Dictionary<ThingCategoryDef, List<ThingDef>> directDefs =
            new Dictionary<ThingCategoryDef, List<ThingDef>>();
        private readonly List<ThingCategoryDef> rootCategories = new List<ThingCategoryDef>();
        private readonly List<ThingDef> rootDefs = new List<ThingDef>();

        internal IEnumerable<ThingCategoryDef> Categories => includedCategories;

        internal QuotaTreeModel(
            IEnumerable<ThingDef> defs,
            IEnumerable<ThingCategoryDef> forcedCategories)
        {
            ThingCategoryDef root = ThingCategoryDefOf.Root;
            foreach (ThingDef def in defs.Where(def => def != null).Distinct())
            {
                ThingCategoryDef category = def.FirstThingCategory;
                if (category == null || category == root)
                {
                    AddDistinct(rootDefs, def);
                    continue;
                }

                if (!directDefs.TryGetValue(category, out List<ThingDef> categoryDefs))
                {
                    categoryDefs = new List<ThingDef>();
                    directDefs.Add(category, categoryDefs);
                }

                AddDistinct(categoryDefs, def);
                AddCategoryPath(category);
            }

            foreach (ThingCategoryDef category in forcedCategories.Where(category => category != null))
            {
                if (category != root)
                {
                    AddCategoryPath(category);
                }
            }

            BuildCategoryChildren();
            rootDefs.Sort(CompareDefs);
            foreach (List<ThingDef> categoryDefs in directDefs.Values)
            {
                categoryDefs.Sort(CompareDefs);
            }
        }

        internal void BuildRows(
            string search,
            ISet<ThingCategoryDef> expandedCategories,
            List<Row> rows)
        {
            rows.Clear();
            string query = search?.Trim() ?? string.Empty;
            bool searching = query.Length > 0;
            Dictionary<ThingCategoryDef, bool> matchCache =
                new Dictionary<ThingCategoryDef, bool>();
            HashSet<ThingCategoryDef> path = new HashSet<ThingCategoryDef>();

            foreach (ThingCategoryDef category in rootCategories)
            {
                AddCategoryRows(
                    category,
                    0,
                    query,
                    searching,
                    false,
                    expandedCategories,
                    matchCache,
                    path,
                    rows);
            }

            foreach (ThingDef def in rootDefs)
            {
                if (!searching || Matches(def, query))
                {
                    rows.Add(new Row
                    {
                        Thing = def,
                        Depth = 0
                    });
                }
            }
        }

        private void AddCategoryPath(ThingCategoryDef category)
        {
            ThingCategoryDef root = ThingCategoryDefOf.Root;
            for (int depth = 0; category != null && depth < 128; depth++)
            {
                includedCategories.Add(category);
                if (category == root)
                {
                    break;
                }

                category = category.parent;
            }
        }

        private void BuildCategoryChildren()
        {
            ThingCategoryDef root = ThingCategoryDefOf.Root;
            foreach (ThingCategoryDef category in includedCategories)
            {
                if (category == root)
                {
                    continue;
                }

                ThingCategoryDef parent = category.parent;
                if (parent == null || parent == root || !includedCategories.Contains(parent))
                {
                    AddDistinct(rootCategories, category);
                    continue;
                }

                if (!childCategories.TryGetValue(parent, out List<ThingCategoryDef> children))
                {
                    children = new List<ThingCategoryDef>();
                    childCategories.Add(parent, children);
                }

                AddDistinct(children, category);
            }

            rootCategories.Sort(CompareCategories);
            foreach (List<ThingCategoryDef> children in childCategories.Values)
            {
                children.Sort(CompareCategories);
            }
        }

        private void AddCategoryRows(
            ThingCategoryDef category,
            int depth,
            string query,
            bool searching,
            bool ancestorMatched,
            ISet<ThingCategoryDef> expandedCategories,
            Dictionary<ThingCategoryDef, bool> matchCache,
            HashSet<ThingCategoryDef> path,
            List<Row> rows)
        {
            if (category == null || depth >= 128 || !path.Add(category))
            {
                return;
            }

            bool categoryMatched = searching && Matches(category, query);
            bool showWholeSubtree = ancestorMatched || categoryMatched;
            if (searching
                && !showWholeSubtree
                && !SubtreeMatches(category, query, matchCache, new HashSet<ThingCategoryDef>()))
            {
                path.Remove(category);
                return;
            }

            bool hasChildren = HasChildren(category);
            rows.Add(new Row
            {
                Category = category,
                Depth = depth,
                HasChildren = hasChildren
            });

            bool expanded = searching || expandedCategories.Contains(category);
            if (hasChildren && expanded)
            {
                if (childCategories.TryGetValue(category, out List<ThingCategoryDef> children))
                {
                    foreach (ThingCategoryDef child in children)
                    {
                        AddCategoryRows(
                            child,
                            depth + 1,
                            query,
                            searching,
                            showWholeSubtree,
                            expandedCategories,
                            matchCache,
                            path,
                            rows);
                    }
                }

                if (directDefs.TryGetValue(category, out List<ThingDef> categoryDefs))
                {
                    foreach (ThingDef def in categoryDefs)
                    {
                        if (!searching || showWholeSubtree || Matches(def, query))
                        {
                            rows.Add(new Row
                            {
                                Thing = def,
                                Depth = depth + 1
                            });
                        }
                    }
                }
            }

            path.Remove(category);
        }

        private bool SubtreeMatches(
            ThingCategoryDef category,
            string query,
            Dictionary<ThingCategoryDef, bool> cache,
            HashSet<ThingCategoryDef> path)
        {
            if (cache.TryGetValue(category, out bool cached))
            {
                return cached;
            }

            if (!path.Add(category))
            {
                return false;
            }

            bool result = Matches(category, query);
            if (!result && directDefs.TryGetValue(category, out List<ThingDef> categoryDefs))
            {
                result = categoryDefs.Any(def => Matches(def, query));
            }

            if (!result && childCategories.TryGetValue(category, out List<ThingCategoryDef> children))
            {
                result = children.Any(child => SubtreeMatches(child, query, cache, path));
            }

            path.Remove(category);
            cache[category] = result;
            return result;
        }

        private bool HasChildren(ThingCategoryDef category)
        {
            return (childCategories.TryGetValue(category, out List<ThingCategoryDef> children)
                    && children.Count > 0)
                || (directDefs.TryGetValue(category, out List<ThingDef> categoryDefs)
                    && categoryDefs.Count > 0);
        }

        private static bool Matches(Def def, string query)
        {
            return def.LabelCap.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CompareCategories(ThingCategoryDef left, ThingCategoryDef right)
        {
            if (ReferenceEquals(left.parent, right.parent) && left.parent?.childCategories != null)
            {
                int leftIndex = left.parent.childCategories.IndexOf(left);
                int rightIndex = right.parent.childCategories.IndexOf(right);
                if (leftIndex >= 0 && rightIndex >= 0 && leftIndex != rightIndex)
                {
                    return leftIndex.CompareTo(rightIndex);
                }
            }

            int byLabel = string.Compare(
                left.LabelCap.ToString(),
                right.LabelCap.ToString(),
                StringComparison.CurrentCulture);
            return byLabel != 0
                ? byLabel
                : string.CompareOrdinal(left.defName, right.defName);
        }

        private static int CompareDefs(ThingDef left, ThingDef right)
        {
            int byLabel = string.Compare(
                left.LabelCap.ToString(),
                right.LabelCap.ToString(),
                StringComparison.CurrentCulture);
            return byLabel != 0
                ? byLabel
                : string.CompareOrdinal(left.defName, right.defName);
        }

        private static void AddDistinct<T>(List<T> list, T value)
        {
            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }
    }
}
