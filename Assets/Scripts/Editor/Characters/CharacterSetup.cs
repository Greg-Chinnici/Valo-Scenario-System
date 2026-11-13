using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using ValoReplay.Replay;

namespace ValoReplay.EditorTools
{
    /// <summary>
    /// ValoReplay > Characters > Set Up Mixamo Character: turns Mixamo downloads in Assets/Characters/Mixamo into the
    /// player character. The character FBX (the one with a mesh) is imported as a Humanoid; every other FBX is imported
    /// as animation on that avatar. Clips are sorted by name (crouch, jump/fall) and placed in the blend trees by how
    /// fast and which way they actually move (their root motion), so file names and pack contents do not matter.
    /// Writes Assets/Characters/PlayerCharacter.controller and Assets/Characters/Resources/PlayerCharacter.asset.
    /// </summary>
    public static class CharacterSetup
    {
        private const string Folder = "Assets/Characters";
        private const string Source = Folder + "/Mixamo";
        private const string ControllerPath = Folder + "/PlayerCharacter.controller";
        private const string RegistryPath = Folder + "/Resources/PlayerCharacter.asset";

        /// <summary>Clips that are not locomotion.</summary>
        private static readonly string[] Skip = { "death", "dying", "dead", "hit", "reaction", "reload", "turn", "throw", "grenade", "melee", "punch", "kick", "equip", "holster", "pickup", "pick up", "t-pose", "tpose" };

        private struct Pick
        {
            public AnimationClip Clip;
            public string File;
            public Vector2 Velocity;     // x right, y forward (m/s)
        }

        [MenuItem("ValoReplay/Characters/Set Up Mixamo Character")]
        public static void SetUp()
        {
            var files = Directory.Exists(Source)
                ? Directory.GetFiles(Source, "*.fbx", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToList()
                : new List<string>();
            if (files.Count == 0)
            {
                EditorUtility.DisplayDialog("Set Up Mixamo Character",
                    $"No .fbx files in {Source}.\n\nDownload a character (with skin) and its animations from Mixamo as \"FBX for Unity\", put them in that folder, and run this again.", "OK");
                return;
            }

            // 1. the character: an FBX with a skinned mesh, preferring one named like a character and with no real motion
            string character = files.Where(HasSkin).OrderBy(p => CharacterRank(p)).FirstOrDefault();
            if (character == null)
            {
                EditorUtility.DisplayDialog("Set Up Mixamo Character", "None of the FBX files has a mesh. Download the character itself from Mixamo with \"With Skin\".", "OK");
                return;
            }
            var ci = (ModelImporter)AssetImporter.GetAtPath(character);
            ci.animationType = ModelImporterAnimationType.Human;
            ci.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            ci.materialImportMode = ModelImporterMaterialImportMode.None;     // the game assigns its own URP material
            ci.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(character).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isHuman)
            {
                EditorUtility.DisplayDialog("Set Up Mixamo Character", $"{Path.GetFileName(character)} could not be mapped to a Humanoid. Check its Rig tab (Configure...) and run this again.", "OK");
                return;
            }

            // 2. every other FBX: animation on the character's avatar, root motion kept (it gives each clip's speed)
            var stand = new List<Pick>();
            var crouch = new List<Pick>();
            var air = new List<Pick>();
            var skipped = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                if (path == character) continue;
                EditorUtility.DisplayProgressBar("Set Up Mixamo Character", Path.GetFileName(path), (float)i / files.Count);
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (Skip.Any(name.Contains)) { skipped.Add(Path.GetFileName(path)); continue; }
                bool isAir = name.Contains("jump") || name.Contains("fall");
                bool isCrouch = name.Contains("crouch");

                var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                mi.sourceAvatar = avatar;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                mi.importAnimation = true;
                var clips = mi.defaultClipAnimations;
                foreach (var c in clips)
                {
                    // in the air: a falling idle loops; a jump or a fall that ends in a landing plays once
                    c.loopTime = !isAir || name.Contains("loop") || (name.Contains("fall") && !name.Contains("land"));
                    c.lockRootRotation = true;  c.keepOriginalOrientation = true;     // no drifting turn
                    c.lockRootHeightY = true;   c.keepOriginalPositionY = true;       // crouch keeps its height
                    c.lockRootPositionXZ = false;                                      // root motion stays: it is the clip's speed
                }
                mi.clipAnimations = clips;
                mi.SaveAndReimport();

                foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
                {
                    if (clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                    var v = new Vector2(clip.averageSpeed.x, clip.averageSpeed.z);
                    if (v.magnitude < 0.25f) v = Vector2.zero;                         // an idle
                    var pick = new Pick { Clip = clip, File = Path.GetFileName(path), Velocity = v };
                    (isAir ? air : isCrouch ? crouch : stand).Add(pick);
                }
            }
            EditorUtility.ClearProgressBar();

            stand = Dedupe(stand);
            crouch = Dedupe(crouch);
            if (stand.Count == 0)
            {
                EditorUtility.DisplayDialog("Set Up Mixamo Character", "No walking, running or idle animations found next to the character. Download a locomotion pack (for example a rifle pack) into the same folder.", "OK");
                return;
            }

            // 3. the animator: standing and crouched blend trees by velocity, a jump/fall state, switched by Crouch / Airborne
            AssetDatabase.DeleteAsset(ControllerPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Airborne", AnimatorControllerParameterType.Bool);
            var sm = ctrl.layers[0].stateMachine;

            var standState = Tree(ctrl, "Locomotion", stand);
            sm.defaultState = standState;
            AnimatorState crouchState = crouch.Count > 0 ? Tree(ctrl, "Crouch", crouch) : null;
            if (crouchState != null)
            {
                Transition(standState.AddTransition(crouchState), AnimatorConditionMode.If, "Crouch", 0.15f);
                Transition(crouchState.AddTransition(standState), AnimatorConditionMode.IfNot, "Crouch", 0.15f);
            }
            // prefer a looping fall, then a fall into a landing, then a whole jump
            int AirRank(Pick a)
            {
                string f = a.File.ToLowerInvariant();
                return f.Contains("loop") || (f.Contains("fall") && !f.Contains("land")) ? 0 : f.Contains("fall") ? 1 : 2;
            }
            var fall = air.OrderBy(AirRank).FirstOrDefault();
            if (fall.Clip != null)
            {
                var airState = sm.AddState("Air");
                airState.motion = fall.Clip;
                var toAir = sm.AddAnyStateTransition(airState);
                toAir.canTransitionToSelf = false;
                Transition(toAir, AnimatorConditionMode.If, "Airborne", 0.1f);
                Transition(airState.AddTransition(standState), AnimatorConditionMode.IfNot, "Airborne", 0.12f);
            }

            // 4. the registry the game loads
            Directory.CreateDirectory(Folder + "/Resources");
            var reg = AssetDatabase.LoadAssetAtPath<CharacterRegistry>(RegistryPath);
            if (reg == null)
            {
                reg = ScriptableObject.CreateInstance<CharacterRegistry>();
                AssetDatabase.CreateAsset(reg, RegistryPath);
            }
            reg.model = AssetDatabase.LoadAssetAtPath<GameObject>(character);
            reg.controller = ctrl;
            reg.standPoints = stand.Select(p => p.Velocity).ToArray();
            reg.crouchPoints = crouch.Select(p => p.Velocity).ToArray();
            EditorUtility.SetDirty(reg);
            AssetDatabase.SaveAssets();
            CharacterRegistry.Reload();

            var report = new StringBuilder();
            report.AppendLine($"Character: {Path.GetFileName(character)}");
            report.AppendLine($"Standing ({stand.Count}): {Describe(stand)}");
            report.AppendLine(crouch.Count > 0 ? $"Crouched ({crouch.Count}): {Describe(crouch)}" : "Crouched: none (players keep the standing animations when crouching)");
            report.AppendLine(fall.Clip != null ? $"In the air: {fall.File}" : "In the air: none");
            if (skipped.Count > 0) report.AppendLine($"Not used: {string.Join(", ", skipped)}");
            if (stand.All(p => p.Velocity == Vector2.zero))
                report.AppendLine("\nEvery clip stands still: were they downloaded \"In Place\"? Download them again with In Place unticked so their speed is known.");
            float top = stand.Count > 0 ? stand.Max(p => p.Velocity.magnitude) : 0f;
            if (top > 0f && top < 5f)
                report.AppendLine($"\nThe fastest clip moves {top:0.0} m/s; players run up to 6.75 m/s, so running plays up to {Mathf.Min(reg.maxPace, 6.75f / top):0.0}x faster to keep the feet planted.");
            if (crouch.Count == 0)
                report.AppendLine("\nFor crouching, add Mixamo clips with \"crouch\" in the name (crouch idle, crouch walk forward / back / left / right) and run this again.");
            else
            {
                var missing = new List<string>();
                if (!AnimatedFigure.Covers(new Vector2(0f, -1f), reg.crouchPoints)) missing.Add("backward");
                if (!AnimatedFigure.Covers(new Vector2(-1f, 0f), reg.crouchPoints)) missing.Add("left");
                if (!AnimatedFigure.Covers(new Vector2(1f, 0f), reg.crouchPoints)) missing.Add("right");
                if (!AnimatedFigure.Covers(new Vector2(0f, 1f), reg.crouchPoints)) missing.Add("forward");
                if (missing.Count > 0)
                    report.AppendLine($"\nNo crouched clip for moving {string.Join(", ", missing)}: those moves use the standing clips until you add them.");
            }
            if (fall.Clip != null && AirRank(fall) > 0)
                report.AppendLine($"\n{fall.File} plays once (it ends on the ground), so a long fall holds its last pose. A looping \"Falling Idle\" fits long falls better.");
            Debug.Log("[Characters] " + report);
            EditorUtility.DisplayDialog("Set Up Mixamo Character", report.ToString(), "OK");
        }

        private static bool HasSkin(string path)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return go != null && go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0;
        }

        /// <summary>Lower is more likely the character itself.</summary>
        private static int CharacterRank(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (n.Contains("bot") || n.Contains("character") || n.Contains("model") || n.Contains("t-pose") || n.Contains("tpose")) return 0;
            var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
            return clips.Any(c => c.length > 0.2f) ? 2 : 1;
        }

        /// <summary>One clip per spot in the blend space (two at the same velocity confuse the blend).</summary>
        private static List<Pick> Dedupe(List<Pick> picks)
        {
            var kept = new List<Pick>();
            foreach (var p in picks.OrderBy(p => p.Velocity == Vector2.zero && p.File.ToLowerInvariant().Contains("idle") ? 0 : 1).ThenBy(p => p.File))
                if (!kept.Any(k => (k.Velocity - p.Velocity).magnitude < 0.3f)) kept.Add(p);
            return kept;
        }

        private static AnimatorState Tree(AnimatorController ctrl, string name, List<Pick> picks)
        {
            var state = ctrl.CreateBlendTreeInController(name, out var tree, 0);
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = "MoveX";
            tree.blendParameterY = "MoveZ";
            foreach (var p in picks) tree.AddChild(p.Clip, p.Velocity);
            return state;
        }

        private static void Transition(AnimatorStateTransition t, AnimatorConditionMode mode, string parameter, float seconds)
        {
            t.hasExitTime = false;
            t.duration = seconds;
            t.AddCondition(mode, 0f, parameter);
        }

        private static string Describe(List<Pick> picks) =>
            string.Join(", ", picks.Select(p => $"{Path.GetFileNameWithoutExtension(p.File)} ({p.Velocity.x:0.0}, {p.Velocity.y:0.0})"));
    }
}
