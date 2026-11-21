#if UNITY_IOS
using System;
using System.Collections.Generic;
using System.IO;

using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Writes the SKAdNetwork identifiers of every ad source LevelPlay mediates (Unity Ads, ironSource and the
    /// networks they bid with) into the generated iOS Info.plist, so iOS 14+ installs stay attributed and the
    /// networks keep bidding on the app. Runs automatically for every iOS build of a project that has the helper.
    ///
    /// The list is compiled in on purpose: LevelPlay's own processor downloads its list at build time, which
    /// fails silently on an offline build agent and leaves the plist without ids, and a text file without a .meta
    /// would be ignored inside a Git package. Ids already in the plist are never duplicated.
    ///
    /// Sources (refresh when Unity announces new ids):
    ///   https://skan.mz.unity3d.com/v3/partner/skadnetworks.plist.json
    ///   https://s3.amazonaws.com/ssa.public/Unity/SKAdNetwork-IDs/All/skadnetworks.ironsource.plist.xml
    /// </summary>
    internal static class LevelPlaySkAdNetworkProcessor
    {
        private const string ItemsKey = "SKAdNetworkItems";
        private const string IdentifierKey = "SKAdNetworkIdentifier";

        private static readonly string[] SkAdNetworkIds =
        {
            "22mmun2rn5.skadnetwork", "238da6jt44.skadnetwork", "252b5q8x7y.skadnetwork", "294l99pt4k.skadnetwork",
            "2fnua5tdw4.skadnetwork", "2u9pt9hc89.skadnetwork", "32z4fx6l9h.skadnetwork", "3qy4746246.skadnetwork",
            "3rd42ekr43.skadnetwork", "3sh42y64q3.skadnetwork", "424m5254lk.skadnetwork", "4468km3ulz.skadnetwork",
            "44jx6755aq.skadnetwork", "488r3q3dtq.skadnetwork", "4dzt52r2t5.skadnetwork", "4fzdc2evr5.skadnetwork",
            "4pfyvq9l8r.skadnetwork", "4w7y6s5ca2.skadnetwork", "578prtvx9j.skadnetwork", "5a6flpkh64.skadnetwork",
            "5f5u5tfb26.skadnetwork", "5l3tpt7t6e.skadnetwork", "5lm9lj6jb7.skadnetwork", "5tjdwbrq8w.skadnetwork",
            "6yxyv74ff7.skadnetwork", "77y3x8wds4.skadnetwork", "7fmhfwg9en.skadnetwork", "7ug5zh24hu.skadnetwork",
            "8s468mfl3y.skadnetwork", "97r2b46745.skadnetwork", "9nlqeag3gk.skadnetwork", "9rd848q2bz.skadnetwork",
            "9t245vhmpl.skadnetwork", "a2p9lx4jpn.skadnetwork", "a8cz6cu7e5.skadnetwork", "av6w8kgt66.skadnetwork",
            "c6k4g5qg8m.skadnetwork", "cstr6suwn9.skadnetwork", "cwn433xbcr.skadnetwork", "e5fvkxwrpn.skadnetwork",
            "f38h382jlk.skadnetwork", "f73kdq92p3.skadnetwork", "f7s53z58qe.skadnetwork", "feyaarzu9v.skadnetwork",
            "g6gcrrvk4p.skadnetwork", "glqzh8vgby.skadnetwork", "hs6bdukanm.skadnetwork", "k674qkevps.skadnetwork",
            "k6y4y55b64.skadnetwork", "kbd757ywx3.skadnetwork", "kbmxgpxpgc.skadnetwork", "klf5c3l5u5.skadnetwork",
            "lr83yxwka7.skadnetwork", "m8dbw4sv7c.skadnetwork", "mj797d8u6f.skadnetwork", "mlmmfzh3r3.skadnetwork",
            "mp6xlyr22a.skadnetwork", "mqn7fxpca7.skadnetwork", "n9x2a789qt.skadnetwork", "p78axxw29g.skadnetwork",
            "ppxm28t8ap.skadnetwork", "prcb7njmu6.skadnetwork", "pwa73g5rt2.skadnetwork", "qqp299437r.skadnetwork",
            "s39g8k73mm.skadnetwork", "su67r6k2v3.skadnetwork", "t38b2kh725.skadnetwork", "tl55sbb4fm.skadnetwork",
            "uw77j35x4d.skadnetwork", "v72qych5uu.skadnetwork", "v79kvwwj4g.skadnetwork", "v9wttpbfk9.skadnetwork",
            "vhf287vqwu.skadnetwork", "w9q455wk68.skadnetwork", "wg4vff78zm.skadnetwork", "wzmmz9fp6w.skadnetwork",
            "x44k69ngh6.skadnetwork", "xga6mpmplv.skadnetwork", "yclnxrl5pm.skadnetwork", "ydx93a7ass.skadnetwork",
            "zmvfpc5aq8.skadnetwork", "zq492l623r.skadnetwork",
        };

        // After LevelPlay's own processor (order 0), before the project-level ones.
        [PostProcessBuild(900)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            var plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);

            PlistElementArray items;
            if (plist.root.values.TryGetValue(ItemsKey, out var existing) && existing is PlistElementArray array)
                items = array;
            else
                items = plist.root.CreateArray(ItemsKey);

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in items.values)
            {
                if (element is PlistElementDict dict &&
                    dict.values.TryGetValue(IdentifierKey, out var id) &&
                    id is PlistElementString text)
                {
                    present.Add(text.value.Trim());
                }
            }

            int added = 0;
            foreach (var id in SkAdNetworkIds)
            {
                if (!present.Add(id))
                    continue;

                items.AddDict().SetString(IdentifierKey, id);
                added++;
            }

            File.WriteAllText(plistPath, plist.WriteToString());
            UnityEngine.Debug.Log($"[LevelPlayHelper] SKAdNetwork: Info.plist lists {present.Count} ids ({added} added by the helper).");
        }
    }
}
#endif
