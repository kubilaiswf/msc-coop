using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.SecondSatsuma;

public class SecondSatsumaMod : Mod
{
    public override string ID => "SecondSatsuma";
    public override string Name => "Second Satsuma";
    public override string Version => "0.1.0";
    public override string Author => "kubilay";
    public override string Description => "Adds a second Satsuma with its own parts and its own save.";

    static bool patched;

    public override void ModSetup() => SetupFunction(Setup.OnMenuLoad, Init);

    // MSCLoader destroys every object, DontDestroyOnLoad ones included, on each return to the menu.
    void Init()
    {
        if (!patched)
        {
            Patch();
            patched = true;
        }
        var watcher = new GameObject("SecondSatsuma");
        UnityEngine.Object.DontDestroyOnLoad(watcher);
        watcher.AddComponent<LevelWatcher>();
    }

    static void Patch()
    {
        var harmony = HarmonyInstance.Create("kubilay.secondsatsuma");
        harmony.Patch(typeof(Fsm).GetMethod("Init", new[] { typeof(MonoBehaviour) }),
            null, new HarmonyMethod(typeof(SecondSatsumaMod).GetMethod(nameof(AfterFsmInit), BindingFlags.NonPublic | BindingFlags.Static)));

        var before = new HarmonyMethod(typeof(SaveKeys).GetMethod(nameof(SaveKeys.Before), BindingFlags.NonPublic | BindingFlags.Static));
        var after = new HarmonyMethod(typeof(SaveKeys).GetMethod(nameof(SaveKeys.After), BindingFlags.NonPublic | BindingFlags.Static));
        var count = 0;
        foreach (var type in SaveKeys.ActionTypes())
        {
            var enter = type.GetMethod("OnEnter", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (enter == null)
                continue;
            harmony.Patch(enter, before, after);
            count++;
        }
        ModConsole.Print("Second Satsuma: " + count + " save actions patched.");
    }

    static void AfterFsmInit(Fsm __instance) => SatsumaCloner.OnFsmInit(__instance);
}

// OnLevelWasLoaded runs after every scene object's Awake but before any Start, so the car and its
// parts are still in the layout of a new game and no save has been read yet, whatever the save holds.
class LevelWatcher : MonoBehaviour
{
    void OnLevelWasLoaded(int level)
    {
        if (Application.loadedLevelName != "GAME")
            return;
        try
        {
            SatsumaCloner.Clone();
        }
        catch (Exception e)
        {
            ModConsole.Error("Second Satsuma: " + e);
        }
    }
}

// The game's save actions build their key from uniqueTag when they enter. For the second car's
// FSMs the tag is swapped for a prefixed copy for the length of that call, which covers tags that
// are literals as well as ones the FSM builds at runtime from part names.
static class SaveKeys
{
    public const string Prefix = "Satsuma2_";

    static readonly Dictionary<Type, FieldInfo> tagFields = new();
    static readonly Stack<KeyValuePair<FsmStateAction, FsmString>> swapped = new();

    public static IEnumerable<Type> ActionTypes()
    {
        foreach (var assembly in new[] { typeof(FsmStateAction).Assembly, typeof(PlayMakerFSM).Assembly, typeof(ES2).Assembly, typeof(CarDynamics).Assembly })
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || !typeof(FsmStateAction).IsAssignableFrom(type) || tagFields.ContainsKey(type))
                    continue;
                var field = type.GetField("uniqueTag", BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.FieldType != typeof(FsmString))
                    continue;
                tagFields[type] = field;
                yield return type;
            }
        }
    }

    internal static void Before(FsmStateAction __instance)
    {
        if (!SatsumaCloner.IsClone(__instance.Fsm) || !tagFields.TryGetValue(__instance.GetType(), out var field))
            return;
        var tag = (FsmString)field.GetValue(__instance);
        if (tag == null || tag.Value == null || tag.Value.StartsWith(Prefix))
            return;
        swapped.Push(new KeyValuePair<FsmStateAction, FsmString>(__instance, tag));
        field.SetValue(__instance, new FsmString { Value = Prefix + tag.Value });
    }

    internal static void After(FsmStateAction __instance)
    {
        if (swapped.Count == 0 || swapped.Peek().Key != __instance)
            return;
        var entry = swapped.Pop();
        tagFields[__instance.GetType()].SetValue(__instance, entry.Value);
    }
}
