// ReSharper disable CheckNamespace
// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

// IMPORTANT: MUST NOT USE A NAMESPACE, otherwise Pulsar/Magnetar won't find the
// Preloader class!
//namespace ServerPlugin;

// Preloader for the DirectTransport server plugin.
//
// The Harmony InitializePatch installs the UDP transport from Plugin.Init, but
// on the dedicated server the engine starts networking (MyNetworkMonitor →
// stock MySteamPeer2Peer) BEFORE it calls IPlugin.Init, so that patch is never
// applied in time and the server crashes with "Steamworks GameServer is not
// initialized". A Harmony patch cannot run early enough here, so instead we
// Cecil-inject a prologue call to DirectServer.PreInstall at preloader time —
// before the game assembly is even loaded — directly into the body of
// MyDedicatedServerBase.Initialize(IPEndPoint). That call then runs at the
// exact moment the Harmony prefix would have, independent of plugin-init order.
//
// The cross-assembly call needs an AssemblyRef to DirectTransport on the
// Sandbox.Game module; the AssemblyResolve handler installed below answers that
// bind at runtime (including Pulsar's randomized dev-folder assembly identity),
// the same machinery se-linux-compat uses for its prepatches.
//
// ReSharper disable once UnusedType.Global
public static class Preloader
{
    private const string SelfSimpleName = "DirectTransport";

    static Preloader()
    {
        var self = typeof(Preloader).Assembly;
        var selfName = self.GetName().Name;
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name == null) return null;
            return name == SelfSimpleName || name == "ServerPlugin" || name == selfName
                ? self
                : null;
        };
    }

    // ReSharper disable once UnusedMember.Global
    public static IEnumerable<string> TargetDLLs { get; } = ["Sandbox.Game.dll"];

    // ReSharper disable once UnusedMember.Global
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Patch(AssemblyDefinition asmDef)
    {
        if (asmDef.Name.Name != "Sandbox.Game")
            return;

        var module = asmDef.MainModule;

        var type = module.GetType("Sandbox.Engine.Multiplayer.MyDedicatedServerBase");
        if (type == null)
        {
            Console.WriteLine("[DirectTransport] Preloader: MyDedicatedServerBase not found (upstream renamed?)");
            return;
        }

        // MyDedicatedServerBase.Initialize(IPEndPoint) — the endpoint the server
        // is about to bind. Instance method: arg0 = this, arg1 = endpoint.
        var init = type.Methods.FirstOrDefault(m =>
            m.Name == "Initialize" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Net.IPEndPoint");
        if (init?.Body == null)
        {
            Console.WriteLine("[DirectTransport] Preloader: MyDedicatedServerBase.Initialize(IPEndPoint) not found");
            return;
        }

        if (init.Body.Instructions.Any(IsPreInstallCall))
        {
            Console.WriteLine("[DirectTransport] Preloader: Initialize already carries the PreInstall prologue (skipping)");
            return;
        }

        // Reuse the method's own IPEndPoint parameter type reference so we don't
        // have to import System.Net.Primitives explicitly.
        var ipEndPointType = init.Parameters[0].ParameterType;
        var preInstall = ImportPreInstall(module, ipEndPointType);

        var il = init.Body.GetILProcessor();
        var first = init.Body.Instructions[0];

        // Prepend:  DirectServer.PreInstall(endpoint);
        //   ldarg.1
        //   call  void [DirectTransport]ServerPlugin.DirectServer::PreInstall(System.Net.IPEndPoint)
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, preInstall));

        Console.WriteLine("[DirectTransport] Preloader: injected DirectServer.PreInstall prologue into MyDedicatedServerBase.Initialize");
    }

    // ReSharper disable once UnusedMember.Global
    public static void Finish()
    {
        // Nothing to do after the per-assembly patch pass.
    }

    private static bool IsPreInstallCall(Instruction instr)
    {
        return instr.OpCode == OpCodes.Call &&
               instr.Operand is MethodReference mr &&
               mr.Name == "PreInstall" &&
               mr.DeclaringType != null &&
               mr.DeclaringType.FullName == "ServerPlugin.DirectServer";
    }

    // Build a MethodReference to DirectTransport's ServerPlugin.DirectServer
    // .PreInstall(IPEndPoint) → void, adding an AssemblyRef to DirectTransport
    // on the target module if one isn't already present.
    private static MethodReference ImportPreInstall(ModuleDefinition module, TypeReference ipEndPointType)
    {
        var directRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == SelfSimpleName);
        if (directRef == null)
        {
            directRef = new AssemblyNameReference(SelfSimpleName, new Version(1, 0, 0, 0))
            {
                PublicKeyToken = Array.Empty<byte>(),
                PublicKey = Array.Empty<byte>(),
                Culture = string.Empty,
                HashAlgorithm = Mono.Cecil.AssemblyHashAlgorithm.None,
            };
            module.AssemblyReferences.Add(directRef);
        }

        var directServerType = new TypeReference("ServerPlugin", "DirectServer", module, directRef, false);

        var method = new MethodReference("PreInstall", module.TypeSystem.Void, directServerType)
        {
            HasThis = false,
            ExplicitThis = false,
            CallingConvention = MethodCallingConvention.Default,
        };
        method.Parameters.Add(new ParameterDefinition(module.ImportReference(ipEndPointType)));
        return method;
    }
}
