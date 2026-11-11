using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class WindowsGpuCapabilityServiceTests
{
    [Fact]
    public void BuildOptionList_AdapterZeroRejected_HasNoSystemDefaultOption()
    {
        // WPF parity (freeze 23ddd271 GpuOption.GetGpuOptions): the system default entry is only
        // added when DXGI adapter 0 itself passed CheckGpu (`if (index == 0)`). When adapter 0 was
        // rejected outright (e.g. no FL11 support, indirect display adapter), the option list
        // starts with Disable followed by the accepted specific GPUs and has no system default.
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 2,
                    Description: "NVIDIA GeForce RTX 4060",
                    InstancePath: "PCI#2",
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 6, 1),
                    DriverVersion: "555.99"),
            ]);

        Assert.Equal(2, options.Count);
        Assert.Equal(GpuOptionKind.Disabled, options[0].Kind);
        Assert.DoesNotContain(options, option => option.Kind == GpuOptionKind.SystemDefault);
        Assert.Equal(GpuOptionKind.SpecificGpu, options[1].Kind);
        Assert.True(options[1].GpuIndex.HasValue);
        Assert.Equal((uint)2, options[1].GpuIndex.Value);
        Assert.Equal("PCI#2", options[1].InstancePath);
    }

    [Fact]
    public void ResolveSelection_AdapterZeroRejected_DefaultPreferenceFallsBackToDisabled()
    {
        // WPF parity (freeze 23ddd271 GpuOption.GetCurrent): with adapter 0 rejected there is no
        // SystemDefaultOption, so GetCurrent's `FirstOrDefault(Disable)` resolves a plain
        // UseGpu=true preference to Disable (CPU OCR) instead of promoting another GPU that the
        // core would never receive (the system default request hard-codes GpuIndex=0).
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 2,
                    Description: "NVIDIA GeForce RTX 4060",
                    InstancePath: "PCI#2",
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 6, 1),
                    DriverVersion: "555.99"),
            ]);
        var preference = new GpuPreference(
            UseGpu: true,
            AllowDeprecatedGpu: false,
            PreferredGpuDescription: string.Empty,
            PreferredGpuInstancePath: string.Empty);

        var selected = WindowsGpuCapabilityService.ResolveSelection(preference, options, out var selectionChanged);

        Assert.False(selectionChanged);
        Assert.Equal(GpuOptionKind.Disabled, selected.Kind);
    }

    [Fact]
    public void ResolveSelection_AdapterZeroRejected_DescriptionOnlyPreferenceFallsBackToDisabled()
    {
        // WPF parity: the description fallback in GetCurrent only runs while the current result
        // is the SystemDefaultOption; without adapter 0 there is no such option, so even a
        // matching description does not promote a specific GPU and resolution stays at Disable.
        // A matching instance path still selects the specific GPU (the path lookup itself is
        // unconditional once a non-empty path was stored).
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 2,
                    Description: "NVIDIA GeForce RTX 4060",
                    InstancePath: "PCI#2",
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 6, 1),
                    DriverVersion: "555.99"),
            ]);

        var byDescription = WindowsGpuCapabilityService.ResolveSelection(
            new GpuPreference(
                UseGpu: true,
                AllowDeprecatedGpu: false,
                PreferredGpuDescription: "NVIDIA GeForce RTX 4060",
                PreferredGpuInstancePath: string.Empty),
            options,
            out var descriptionChanged);

        Assert.True(descriptionChanged);
        Assert.Equal(GpuOptionKind.Disabled, byDescription.Kind);

        var byPath = WindowsGpuCapabilityService.ResolveSelection(
            new GpuPreference(
                UseGpu: true,
                AllowDeprecatedGpu: false,
                PreferredGpuDescription: "NVIDIA GeForce RTX 4060",
                PreferredGpuInstancePath: "PCI#2"),
            options,
            out var pathChanged);

        Assert.False(pathChanged);
        Assert.Equal(GpuOptionKind.SpecificGpu, byPath.Kind);
        Assert.Equal((uint)2, byPath.GpuIndex);
    }

    [Fact]
    public void ResolveSelection_EmptyStoredInstancePath_DoesNotMatchOptionWithUnresolvedInstancePath()
    {
        // WPF parity (freeze 23ddd271 GpuOption.GetCurrent): the instance-path lookup is skipped
        // when the stored path is empty (`preferredGpuInstancePath != string.Empty`). An option
        // whose own instance path could not be resolved must not capture a description-only
        // preference (string.Equals("", "", Ordinal) is always true); the description fallback
        // has to select the intended GPU instead.
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 0,
                    Description: "GPU A",
                    InstancePath: string.Empty,
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 1, 1),
                    DriverVersion: "1.0"),
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 1,
                    Description: "GPU B",
                    InstancePath: "PCI#1",
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 1, 1),
                    DriverVersion: "1.0"),
            ]);
        var preference = new GpuPreference(
            UseGpu: true,
            AllowDeprecatedGpu: false,
            PreferredGpuDescription: "GPU B",
            PreferredGpuInstancePath: string.Empty);

        var selected = WindowsGpuCapabilityService.ResolveSelection(preference, options, out var selectionChanged);

        Assert.False(selectionChanged);
        Assert.Equal(GpuOptionKind.SpecificGpu, selected.Kind);
        Assert.Equal("GPU B", selected.Description);
        Assert.Equal((uint)1, selected.GpuIndex);
    }

    [Fact]
    public void BuildOptionList_MissingInstancePathStillProducesSpecificGpuOption()
    {
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 3,
                    Description: "AMD Radeon RX 7800 XT",
                    InstancePath: string.Empty,
                    IsDeprecated: false,
                    DriverDate: null,
                    DriverVersion: null),
            ]);

        var specific = Assert.Single(options.Where(option => option.Kind == GpuOptionKind.SpecificGpu));
        Assert.Equal(string.Empty, specific.InstancePath);
        Assert.True(specific.GpuIndex.HasValue);
        Assert.Equal((uint)3, specific.GpuIndex.Value);
        Assert.True(specific.Id.StartsWith("DXGI#3#AMD Radeon RX 7800 XT", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildOptionList_KeepsDeprecatedGpus_SystemDefaultAlwaysDescribesAdapterZero()
    {
        // WPF parity (freeze 23ddd271 GpuOption.GetGpuOptions): deprecated GPUs stay in the
        // option list; AllowDeprecatedGpu only hides them in the picker UI. The system default
        // entry must describe adapter 0 even when it is deprecated, because the core always
        // receives GpuIndex=0 for the system default selection.
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 0,
                    Description: "Legacy GPU",
                    InstancePath: "PCI#0",
                    IsDeprecated: true,
                    DriverDate: new DateTime(2018, 1, 1),
                    DriverVersion: "1.0"),
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 1,
                    Description: "Intel Arc A770",
                    InstancePath: "PCI#1",
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 1, 1),
                    DriverVersion: "31.0"),
            ]);

        Assert.Equal(4, options.Count);
        Assert.Equal(GpuOptionKind.Disabled, options[0].Kind);
        Assert.Equal(GpuOptionKind.SystemDefault, options[1].Kind);
        Assert.Equal("Legacy GPU", options[1].Description);
        Assert.True(options[1].IsDeprecated);
        Assert.Equal((uint)0, options[1].GpuIndex);
        var legacy = Assert.Single(options.Where(option => option.InstancePath == "PCI#0"));
        Assert.True(legacy.IsDeprecated);
        Assert.Equal((uint)0, legacy.GpuIndex);
        var arc = Assert.Single(options.Where(option => option.InstancePath == "PCI#1"));
        Assert.False(arc.IsDeprecated);
        Assert.Equal((uint)1, arc.GpuIndex);
    }

    [Fact]
    public void ResolveSelection_AllowDeprecatedFalse_StillMatchesDeprecatedSpecificSelection()
    {
        // WPF parity (freeze 23ddd271 GpuOption.GetCurrent): a stored specific selection that
        // points at a deprecated GPU is still honored; AllowDeprecatedGpu must not turn it into
        // a SystemDefault fallback (which would rewrite the user's stored choice on save).
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 0,
                    Description: "Legacy GPU",
                    InstancePath: "PCI#0",
                    IsDeprecated: true,
                    DriverDate: new DateTime(2018, 1, 1),
                    DriverVersion: "1.0"),
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 1,
                    Description: "Intel Arc A770",
                    InstancePath: "PCI#1",
                    IsDeprecated: false,
                    DriverDate: new DateTime(2024, 1, 1),
                    DriverVersion: "31.0"),
            ]);
        var preference = new GpuPreference(
            UseGpu: true,
            AllowDeprecatedGpu: false,
            PreferredGpuDescription: "Legacy GPU",
            PreferredGpuInstancePath: "PCI#0");

        var selected = WindowsGpuCapabilityService.ResolveSelection(preference, options, out var selectionChanged);

        Assert.False(selectionChanged);
        Assert.Equal(GpuOptionKind.SpecificGpu, selected.Kind);
        Assert.Equal("PCI#0", selected.InstancePath);
        Assert.True(selected.IsDeprecated);
        Assert.Equal((uint)0, selected.GpuIndex);
    }

    [Fact]
    public void ResolveSelection_AllowDeprecatedFalse_OnlyDeprecatedAdapterZero_SystemDefaultStillResolves()
    {
        // WPF parity: a machine whose only GPU is deprecated (e.g. an Intel Xe iGPU) still gets
        // GPU OCR through the system default selection; filtering here collapsed the option list
        // to Disabled and silently switched such machines to CPU OCR.
        var options = WindowsGpuCapabilityService.BuildOptionList(
            candidates:
            [
                new WindowsGpuCapabilityService.WindowsGpuCandidate(
                    AdapterIndex: 0,
                    Description: "Intel(R) Iris(R) Xe Graphics",
                    InstancePath: "PCI#0",
                    IsDeprecated: true,
                    DriverDate: new DateTime(2023, 1, 1),
                    DriverVersion: "31.0"),
            ]);
        var preference = new GpuPreference(
            UseGpu: true,
            AllowDeprecatedGpu: false,
            PreferredGpuDescription: string.Empty,
            PreferredGpuInstancePath: string.Empty);

        var selected = WindowsGpuCapabilityService.ResolveSelection(preference, options, out var selectionChanged);

        Assert.False(selectionChanged);
        Assert.Equal(GpuOptionKind.SystemDefault, selected.Kind);
        Assert.True(selected.IsDeprecated);
        Assert.Equal((uint)0, selected.GpuIndex);
    }

    [Fact]
    public void BuildPhysicalAdapterIdentity_PrefersInstancePath()
    {
        var identity = WindowsGpuCapabilityService.BuildPhysicalAdapterIdentity(
            new WindowsGpuCapabilityService.WindowsGpuCandidate(
                AdapterIndex: 0,
                Description: "Intel(R) Iris(R) Xe Graphics",
                InstancePath: "PCI#8086",
                IsDeprecated: false,
                DriverDate: new DateTime(2025, 1, 1),
                DriverVersion: "32.0.101.7082",
                AdapterLuidLowPart: 123,
                AdapterLuidHighPart: 456));

        Assert.Equal("PNP:PCI#8086", identity);
    }

    [Fact]
    public void BuildPhysicalAdapterIdentity_FallsBackToLuidWhenInstancePathMissing()
    {
        var identity = WindowsGpuCapabilityService.BuildPhysicalAdapterIdentity(
            new WindowsGpuCapabilityService.WindowsGpuCandidate(
                AdapterIndex: 1,
                Description: "Intel(R) Iris(R) Xe Graphics",
                InstancePath: string.Empty,
                IsDeprecated: false,
                DriverDate: new DateTime(2025, 1, 1),
                DriverVersion: "32.0.101.7082",
                AdapterLuidLowPart: 123,
                AdapterLuidHighPart: 456));

        Assert.Equal("LUID:456:123", identity);
    }

    [Fact]
    public void BuildPhysicalAdapterIdentity_SameInstancePathProducesSameIdentityAcrossAdapterIndices()
    {
        var first = WindowsGpuCapabilityService.BuildPhysicalAdapterIdentity(
            new WindowsGpuCapabilityService.WindowsGpuCandidate(
                AdapterIndex: 0,
                Description: "Intel(R) Iris(R) Xe Graphics",
                InstancePath: "PCI#8086",
                IsDeprecated: false,
                DriverDate: new DateTime(2025, 1, 1),
                DriverVersion: "32.0.101.7082",
                AdapterLuidLowPart: 123,
                AdapterLuidHighPart: 456));
        var second = WindowsGpuCapabilityService.BuildPhysicalAdapterIdentity(
            new WindowsGpuCapabilityService.WindowsGpuCandidate(
                AdapterIndex: 1,
                Description: "Intel(R) Iris(R) Xe Graphics",
                InstancePath: "PCI#8086",
                IsDeprecated: false,
                DriverDate: new DateTime(2025, 1, 1),
                DriverVersion: "32.0.101.7082",
                AdapterLuidLowPart: 999,
                AdapterLuidHighPart: 888));

        Assert.Equal(first, second);
    }

    [Fact]
    public void IntelBlacklist_MatchesWpfFreeze_XeAndXeLpgAreDeprecatedByDefault()
    {
        // WPF parity (freeze 23ddd271 GpuOption.cs IntelBlacklist): Xe (Tiger Lake..Raptor Lake)
        // and Xe-LPG (Meteor Lake) devices are deprecated and hidden unless AllowDeprecatedGpu is set.
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0xA780)); // Xe, first listed
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0xA7A0)); // Xe (Raptor Lake-P)
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0x9A49)); // Xe, last listed
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0x7D40)); // Xe-LPG (Meteor Lake)
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0x7DD5)); // Xe-LPG, last listed
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0x8A70)); // Gen11
        Assert.True(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0x190E)); // Gen9, last listed
        Assert.False(WindowsGpuCapabilityService.IsIntelDeviceBlacklistedByDefault(0x56A0)); // Arc Alchemist, not listed
    }

    [Fact]
    public void IsProbablyIndirectDisplayAdapter_OrayIddDriver_IsRecognizedEarly()
    {
        Assert.True(WindowsGpuCapabilityService.IsProbablyIndirectDisplayAdapter("OrayIddDriver Device", string.Empty));
    }

    [Fact]
    public void IsProbablyIndirectDisplayAdapter_IntelXeGraphics_IsNotFlagged()
    {
        Assert.False(
            WindowsGpuCapabilityService.IsProbablyIndirectDisplayAdapter(
                "Intel(R) Iris(R) Xe Graphics",
                @"PCI\VEN_8086&DEV_A7A0&SUBSYS_50D117AA&REV_04\3&11583659&1&10"));
    }
}
