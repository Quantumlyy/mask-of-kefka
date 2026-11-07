using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Capture;

/// <summary>
/// Access to the game's textures via FFXIVClientStructs. Everything here is patch-sensitive:
/// see .dev/UPDATING.md before touching it.
/// </summary>
internal static unsafe class GameSources
{
    /// <summary>
    /// Offset where the Texture* pointer table starts inside RenderTargetManager
    /// (right after the vtable/header, at the _gBuffers field). Verify it in
    /// FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs after each patch.
    /// </summary>
    internal const int RenderTargetTableOffset = 0x20;

    /// <summary>
    /// Not every slot in the table is a texture pointer: the struct mixes Texture* with
    /// resolution fields, flags, floats and pointers to other classes. A slot is only used
    /// after <see cref="TryResolveTexture"/> proved it points at a live Texture, so the
    /// range can safely cover the whole struct.
    /// </summary>
    internal static int RenderTargetSlotCount => (sizeof(RenderTargetManager) - RenderTargetTableOffset) / 8;

    internal static Device* GameDevice => Device.Instance();

    /// <summary>Game backbuffer: scene + game UI, still without ImGui/Dalamud.</summary>
    internal static bool TryGetBackBuffer(out ID3D11Texture2D* texture)
    {
        texture = null;
        var dev = Device.Instance();
        if (dev == null || dev->SwapChain == null)
            return false;

        return TryResolveTexture(dev->SwapChain->BackBuffer, out texture);
    }

    /// <summary>Arbitrary game render target ("no UI" mode).</summary>
    internal static bool TryGetRenderTarget(int index, out ID3D11Texture2D* texture)
    {
        texture = null;

        if (index < 0 || index >= RenderTargetSlotCount)
            return false;

        var rtm = RenderTargetManager.Instance();
        if (rtm == null)
            return false;

        var slots = (Texture**)((byte*)rtm + RenderTargetTableOffset);
        return TryResolveTexture(slots[index], out texture);
    }

    /// <summary>Maintenance tool: lists the plausible slots to the log to find the scene index.</summary>
    internal static void DumpRenderTargets(IPluginLog log)
    {
        var rtm = RenderTargetManager.Instance();
        if (rtm == null)
        {
            log.Warning("RenderTargetManager unavailable.");
            return;
        }

        uint swapWidth = 0, swapHeight = 0;
        void* backBuffer = null;
        var dev = Device.Instance();
        if (dev != null && dev->SwapChain != null)
        {
            swapWidth = dev->SwapChain->Width;
            swapHeight = dev->SwapChain->Height;
            if (TryResolveTexture(dev->SwapChain->BackBuffer, out var bb))
                backBuffer = bb;
        }

        log.Information($"RenderTargetManager @ {(nuint)rtm:X}, {RenderTargetSlotCount} slots starting at +0x{RenderTargetTableOffset:X}, swapchain {swapWidth}x{swapHeight}:");
        log.Information("  Candidates for the no-UI mode have the swapchain resolution, a color format and srv=yes. '*' marks them.");

        var slots = (Texture**)((byte*)rtm + RenderTargetTableOffset);
        var listed = 0;
        for (var i = 0; i < RenderTargetSlotCount; i++)
        {
            var t = slots[i];
            if (!TryResolveTexture(t, out var d3dTexture))
                continue;

            var sameSize = t->ActualWidth == swapWidth && t->ActualHeight == swapHeight;
            var candidate = sameSize && IsColorFormat(t->TextureFormat) && t->D3D11ShaderResourceView != null;
            var note = d3dTexture == backBuffer ? " (swapchain backbuffer: UI included)" : "";
            log.Information($"  {(candidate ? '*' : ' ')}[{i,3}] +0x{RenderTargetTableOffset + i * 8:X3} {t->ActualWidth}x{t->ActualHeight} fmt={t->TextureFormat} srv={(t->D3D11ShaderResourceView != null ? "yes" : "no")}{note}");
            listed++;
        }

        log.Information($"  {listed} live textures listed. Index N lives at RenderTargetManager+0x{RenderTargetTableOffset:X}+N*8; cross-reference RenderTargetManager.cs in FFXIVClientStructs for names.");
    }

    private static bool IsColorFormat(TextureFormat format)
    {
        // Depth/stencil formats live in the 0x4xxx/0x5xxx/0x8xxx families; everything else is color data.
        var family = (uint)format >> 12;
        return family is not (4 or 5 or 8);
    }

    /// <summary>
    /// Turns a possibly-garbage Texture* into its ID3D11Texture2D, or returns false. Every
    /// dereference is guarded: the slot might hold an integer pair, a float, a stale pointer
    /// or an object of another class. An access violation here takes the whole game down
    /// (it cannot be caught in .NET), hence the paranoia.
    /// </summary>
    internal static bool TryResolveTexture(Texture* t, out ID3D11Texture2D* texture)
    {
        texture = null;

        if (!LooksLikePointer(t))
            return false;

        // Four VirtualQuery calls per frame (a few microseconds). Deliberately not cached:
        // the slots change on resolution switches and device resets, and a stale cache
        // would defeat the whole point of the check.

        // 1. The Texture struct itself must be committed, readable memory.
        if (!IsReadable(t, (nuint)sizeof(Texture)))
            return false;

        // 2. A live game object starts with a vtable pointer into a loaded image (ffxiv_dx11.exe).
        if (!IsImagePointer(*(void**)t))
            return false;

        // 3. Plausible 2D render target dimensions.
        if (t->ActualWidth == 0 || t->ActualWidth > 16384 || t->ActualHeight == 0 || t->ActualHeight > 16384)
            return false;

        // 4. The D3D11 object must be a readable COM object whose vtable lives in a loaded image (d3d11.dll).
        var d3d = t->D3D11Texture2D;
        if (!LooksLikePointer(d3d) || !IsReadable(d3d, (nuint)sizeof(void*)) || !IsImagePointer(*(void**)d3d))
            return false;

        // The SRV is only read as a flag by the dump; it must still not be a wild pointer if present.
        var srv = t->D3D11ShaderResourceView;
        if (srv != null && !LooksLikePointer(srv))
            return false;

        texture = (ID3D11Texture2D*)d3d;
        return true;
    }

    /// <summary>
    /// Cheap pre-filter: require a canonical, 8-aligned user-space address. Scalars packed in a
    /// slot (for example a width/height pair such as 1920x1080) can still pass this, which is why
    /// <see cref="IsReadable"/> and <see cref="IsImagePointer"/> exist.
    /// </summary>
    private static bool LooksLikePointer(void* p)
        => p != null && ((nuint)p & 0x7) == 0 && (nuint)p > 0x10000 && (nuint)p < 0x7FFF_FFFF_0000;

    /// <summary>True when every byte of [p, p+size) is committed and readable.</summary>
    private static bool IsReadable(void* p, nuint size)
    {
        var cursor = (byte*)p;
        var end = cursor + size;
        while (cursor < end)
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(cursor, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0)
                return false;
            if (mbi.State != MEM.MEM_COMMIT || !IsReadableProtection(mbi.Protect))
                return false;

            cursor = (byte*)mbi.BaseAddress + mbi.RegionSize;
        }

        return true;
    }

    /// <summary>True when p points into readable memory mapped from an executable image (exe/dll).</summary>
    private static bool IsImagePointer(void* p)
    {
        if (!LooksLikePointer(p))
            return false;

        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery(p, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0)
            return false;

        return mbi.State == MEM.MEM_COMMIT && mbi.Type == MEM.MEM_IMAGE && IsReadableProtection(mbi.Protect);
    }

    private static bool IsReadableProtection(uint protect)
    {
        if ((protect & (PAGE.PAGE_GUARD | PAGE.PAGE_NOACCESS)) != 0)
            return false;

        const uint readable = PAGE.PAGE_READONLY | PAGE.PAGE_READWRITE | PAGE.PAGE_WRITECOPY
                              | PAGE.PAGE_EXECUTE_READ | PAGE.PAGE_EXECUTE_READWRITE | PAGE.PAGE_EXECUTE_WRITECOPY;
        return (protect & readable) != 0;
    }
}
