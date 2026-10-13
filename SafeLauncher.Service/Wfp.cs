using System;
using System.Runtime.InteropServices;

namespace SafeLauncher.Service;

/// <summary>
/// Фильтры Windows (Windows Filtering Platform, fwpuclnt.dll): только то, что нужно <see cref="NetworkGuard"/>.
/// Раскладка структур — как в fwpmtypes.h для 64-битной Windows (лаунчер и служба собираются только под x64).
/// </summary>
internal static class Wfp
{
    public const uint SessionFlagDynamic = 0x1;
    public const uint RpcAuthnDefault = 0xFFFFFFFF;

    public const uint MatchEqual = 0;
    public const uint MatchFlagsAllSet = 6;

    public const uint TypeUint8 = 1;
    public const uint TypeUint16 = 2;
    public const uint TypeUint32 = 3;
    public const uint TypeUint64 = 4;
    public const uint TypeByteBlob = 12;

    public const uint ActionBlock = 0x1 | 0x1000;
    public const uint ActionPermit = 0x2 | 0x1000;

    public const uint ConditionFlagIsLoopback = 0x1;

    public const uint ErrorAlreadyExists = 0x80320009;

    public static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid LayerRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    public static readonly Guid LayerRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    public static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    public static readonly Guid ConditionLocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    public static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    public static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayData
    {
        public IntPtr Name;
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByteBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    /// <summary>FWP_VALUE0 / FWP_CONDITION_VALUE0: тип и объединение (число или указатель).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Value
    {
        public uint Type;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Session
    {
        public Guid SessionKey;
        public DisplayData DisplayData;
        public uint Flags;
        public uint TxnWaitTimeoutInMSec;
        public uint ProcessId;
        public IntPtr Sid;
        public IntPtr Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SubLayer
    {
        public Guid SubLayerKey;
        public DisplayData DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public ByteBlob ProviderData;
        public ushort Weight;
    }

    /// <summary>FWPM_FILTER_CONDITION0, 40 байт.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FilterCondition
    {
        public Guid FieldKey;
        public uint MatchType;
        public Value ConditionValue;
    }

    /// <summary>FWPM_FILTER0, 200 байт. Смещения явно: объединение rawContext/providerContextKey выровнено на 8.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 200)]
    public struct Filter
    {
        [FieldOffset(0)] public Guid FilterKey;
        [FieldOffset(16)] public DisplayData DisplayData;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public IntPtr ProviderKey;
        [FieldOffset(48)] public ByteBlob ProviderData;
        [FieldOffset(64)] public Guid LayerKey;
        [FieldOffset(80)] public Guid SubLayerKey;
        [FieldOffset(96)] public Value Weight;
        [FieldOffset(112)] public uint NumFilterConditions;
        [FieldOffset(120)] public IntPtr FilterCondition;
        [FieldOffset(128)] public uint ActionType;
        [FieldOffset(132)] public Guid ActionFilterType;
        [FieldOffset(152)] public ulong RawContext;
        [FieldOffset(160)] public ulong RawContextHigh;
        [FieldOffset(168)] public IntPtr Reserved;
        [FieldOffset(176)] public ulong FilterId;
        [FieldOffset(184)] public Value EffectiveWeight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, ref Session session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref SubLayer subLayer, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref Filter filter, IntPtr securityDescriptor, out ulong id);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    public static extern void FwpmFreeMemory0(ref IntPtr p);

    [DllImport("iphlpapi.dll")]
    public static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong luid);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint ConvertInterfaceAliasToLuid(string alias, out ulong luid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryDosDeviceW(string deviceName, char[] targetPath, uint max);
}
