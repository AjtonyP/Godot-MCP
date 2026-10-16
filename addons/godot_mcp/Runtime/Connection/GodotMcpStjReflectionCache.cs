/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Godot-MCP)    │
│  Copyright (c) 2026 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace com.IvanMurzak.Godot.MCP.Connection
{
    /// <summary>
    /// Releases System.Text.Json's PROCESS-WIDE reflection caches on plugin teardown — a key root of
    /// godotengine/godot#78513.
    ///
    /// <para>
    /// <b>The pin (two independent cache families, both in the NON-collectible System.Text.Json assembly).</b>
    /// <list type="number">
    ///   <item><b>Member-accessor cache</b> — <c>ReflectionEmitCachingMemberAccessor.s_cache</c> holds compiled
    ///   getter/setter delegates keyed by <c>(string id, Type declaringType, MemberInfo)</c>. Any addon type
    ///   (de)serialized through the reflection path lands in it, pinning the collectible context.</item>
    ///   <item><b>Per-options type-info cache</b> — every live <see cref="JsonSerializerOptions"/> owns a
    ///   <c>CachingContext</c> whose <c>JsonTypeInfo</c>/<c>JsonPropertyInfo</c> objects reference resolved
    ///   addon <c>Type</c>s. <c>JsonSerializerOptions.Default</c> plus any long-lived options instance held
    ///   by ReflectorNet/McpPlugin is a static-lifetime root.</item>
    /// </list>
    /// Clearing only one family is NOT sufficient (verified: a collectible-ALC type serialized once stays
    /// pinned after a member-accessor-only clear).
    /// </para>
    ///
    /// <para>
    /// <b>The fix.</b> On the reload-safe teardown we invoke the runtime's OWN process-wide flush,
    /// <c>JsonSerializerOptionsUpdateHandler.ClearCache(Type[]?)</c> (internal; the entry point .NET itself
    /// calls from its metadata-update / hot-reload handler). It clears BOTH families:
    /// <c>foreach (options in JsonSerializerOptions.TrackedOptionsInstances.All) options.ClearCaches();</c>
    /// followed by the member-accessor clear. STJ rebuilds entries lazily on next use.
    /// </para>
    ///
    /// <para>
    /// <b>Version notes.</b> The update handler exists on .NET 8, 9 and 10. On .NET 8 the member-accessor
    /// clear is <c>ReflectionEmitCachingMemberAccessor.Clear()</c> — a <b>static</b> method (on .NET 9+ it is
    /// an instance override plus <c>DefaultJsonTypeInfoResolver.ClearMemberAccessorCaches()</c>). A fallback
    /// that only looks for an INSTANCE <c>Clear()</c> silently no-ops on .NET 8 (the runtime the Godot 4.x
    /// .NET editor uses) — hence the layered implementation below.
    /// </para>
    ///
    /// <para>
    /// STJ internals are not public API, so the whole operation is reflection-based and fully defensive:
    /// any failure (a future STJ refactor, an AOT/no-emit runtime that never populated the cache) is
    /// swallowed, leaving behavior no worse than before. Pure-BCL (no Godot types) so it is CI-unit-testable.
    /// </para>
    /// </summary>
    public static class GodotMcpStjReflectionCache
    {
        /// <summary>Optional diagnostic sink (e.g. <c>GD.Print</c>); stays pure-BCL by taking a delegate.</summary>
        public static Action<string>? Log { get; set; }

        /// <summary>
        /// Clear System.Text.Json's process-wide reflection caches. Best-effort and never throws.
        /// Returns <c>true</c> only if BOTH cache families were located and cleared, <c>false</c> otherwise (so
        /// a unit test can assert the reflection path still resolves against the running STJ build).
        /// </summary>
        public static bool Clear()
        {
            try
            {
                // Preferred (all supported runtimes): the runtime's own process-wide flush — the same entry
                // point .NET's metadata-update/hot-reload handler uses. Clears every tracked options'
                // type-info cache AND the member-accessor cache in one call.
                var updateHandler = typeof(JsonSerializer).Assembly.GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler");
                var clearCache = updateHandler?.GetMethod("ClearCache",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, types: new[] { typeof(Type[]) }, modifiers: null);
                if (clearCache != null)
                {
                    clearCache.Invoke(null, new object?[] { null });
                    SafeLog("[Godot-MCP] STJ clear: JsonSerializerOptionsUpdateHandler.ClearCache() invoked (all tracked options caches + member-accessor cache).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                SafeLog($"[Godot-MCP] STJ clear: update-handler flush failed, trying manual fallback: {Describe(ex)}");
            }

            return ClearManually();
        }

        /// <summary>
        /// Manual fallback for a runtime without the update handler: clear every tracked options' type-info
        /// cache AND the member-accessor cache. Both families must be cleared (one alone leaves the ALC
        /// pinned), so this returns <c>true</c> only when both succeeded. Internal so tests can exercise it
        /// directly — on every currently supported runtime <see cref="Clear"/> never reaches it.
        /// </summary>
        internal static bool ClearManually()
        {
            // Run both even if the first fails: a partial clear still releases what it can.
            bool clearedOptions = ClearTrackedOptionsCaches();
            bool clearedAccessor = ClearMemberAccessorCache();
            return clearedOptions && clearedAccessor;
        }

        /// <summary>
        /// Clear the <c>CachingContext</c> type-info cache of every live <see cref="JsonSerializerOptions"/>
        /// by walking <c>JsonSerializerOptions.TrackedOptionsInstances.All</c> (the internal
        /// <c>ConditionalWeakTable</c> the runtime itself enumerates) and invoking the internal instance
        /// <c>ClearCaches()</c> on each. Best-effort; returns <c>true</c> when the table was walked. Never
        /// throws (callers are on the ALC-unloading path).
        /// </summary>
        static bool ClearTrackedOptionsCaches()
        {
            try
            {
                var optionsType = typeof(JsonSerializerOptions);
                var trackedType = optionsType.GetNestedType("TrackedOptionsInstances", BindingFlags.NonPublic);
                var all = trackedType?.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    as IEnumerable<KeyValuePair<JsonSerializerOptions, object?>>;
                if (all == null)
                {
                    SafeLog("[Godot-MCP] STJ clear: TrackedOptionsInstances.All not found.");
                    return false;
                }

                var clearCaches = optionsType.GetMethod("ClearCaches",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (clearCaches == null)
                {
                    SafeLog("[Godot-MCP] STJ clear: JsonSerializerOptions.ClearCaches() not found.");
                    return false;
                }

                int cleared = 0;
                foreach (var entry in all)
                {
                    clearCaches.Invoke(entry.Key, null);
                    cleared++;
                }

                SafeLog($"[Godot-MCP] STJ clear: cleared {cleared} tracked JsonSerializerOptions cache(s).");
                return true;
            }
            catch (Exception ex)
            {
                SafeLog($"[Godot-MCP] STJ clear: tracked-options clear failed (ignored): {Describe(ex)}");
                return false;
            }
        }

        /// <summary>
        /// Clear the reflection-emit member-accessor cache, trying each known runtime shape in turn:
        /// <c>DefaultJsonTypeInfoResolver.ClearMemberAccessorCaches()</c> (.NET 9+), the <b>static</b>
        /// <c>ReflectionEmitCachingMemberAccessor.Clear()</c> (.NET 8), then an instance <c>Clear()</c> on the
        /// resolver's singleton accessor. Best-effort; never throws.
        /// </summary>
        static bool ClearMemberAccessorCache()
        {
            try
            {
                var stj = typeof(JsonSerializer).Assembly;
                var resolverType = stj.GetType("System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver");

                var clearAll = resolverType?.GetMethod("ClearMemberAccessorCaches",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (clearAll != null)
                {
                    clearAll.Invoke(null, null);
                    SafeLog("[Godot-MCP] STJ clear: DefaultJsonTypeInfoResolver.ClearMemberAccessorCaches() invoked.");
                    return true;
                }

                var cachingType = stj.GetType("System.Text.Json.Serialization.Metadata.ReflectionEmitCachingMemberAccessor");
                var staticClear = cachingType?.GetMethod("Clear",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (staticClear != null)
                {
                    staticClear.Invoke(null, null);
                    SafeLog("[Godot-MCP] STJ clear: ReflectionEmitCachingMemberAccessor.Clear() (static) invoked.");
                    return true;
                }

                object? accessor =
                    resolverType?.GetProperty("MemberAccessor", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? resolverType?.GetField("s_memberAccessor", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (accessor == null)
                {
                    SafeLog("[Godot-MCP] STJ clear: member accessor not found.");
                    return false;
                }

                var instanceClear = accessor.GetType().GetMethod("Clear",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (instanceClear == null)
                {
                    SafeLog($"[Godot-MCP] STJ clear: no Clear() on {accessor.GetType().Name} (no-emit runtime?).");
                    return false;
                }

                instanceClear.Invoke(accessor, null);
                SafeLog($"[Godot-MCP] STJ clear: {accessor.GetType().Name}.Clear() (instance) invoked.");
                return true;
            }
            catch (Exception ex)
            {
                SafeLog($"[Godot-MCP] STJ clear: member-accessor clear failed (ignored): {Describe(ex)}");
                return false;
            }
        }

        /// <summary>
        /// Invoke <see cref="Log"/> without letting a throwing sink (e.g. <c>GD.Print</c> on a disposed engine
        /// object during unload) turn a successful clear into a reported failure.
        /// </summary>
        static void SafeLog(string message)
        {
            try { Log?.Invoke(message); } catch { /* never throw into the ALC-unloading handler */ }
        }

        /// <summary>
        /// <see cref="MethodBase.Invoke(object, object[])"/> wraps the real STJ failure in a
        /// <see cref="TargetInvocationException"/> whose message is generic; log the inner exception instead.
        /// </summary>
        static string Describe(Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            return $"{inner.GetType().Name}: {inner.Message}";
        }
    }
}
