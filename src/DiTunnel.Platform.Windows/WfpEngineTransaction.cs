using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace DiTunnel.Platform.Windows;

/// <summary>Runs WFP mutations atomically and never leaves a partial filter set behind.</summary>
internal static unsafe class WfpEngineTransaction
{
    private const uint RpcCAuthnWinnt = 10;
    internal static void Execute(Action<FWPM_ENGINE_HANDLE> mutation)
    {
        FWPM_ENGINE_HANDLE engine = default;
        ThrowIfFailed(PInvoke.FwpmEngineOpen0(null, RpcCAuthnWinnt, null, null, &engine));
        var transactionStarted = false;
        try
        {
            ThrowIfFailed(PInvoke.FwpmTransactionBegin0(engine, 0));
            transactionStarted = true;
            mutation(engine);
            ThrowIfFailed(PInvoke.FwpmTransactionCommit0(engine));
            transactionStarted = false;
        }
        finally
        {
            if (transactionStarted) _ = PInvoke.FwpmTransactionAbort0(engine);
            _ = PInvoke.FwpmEngineClose0(engine);
        }
    }

    internal static void ThrowIfFailed(uint result)
    {
        if (result != 0) throw new Win32Exception(unchecked((int)result));
    }
}
