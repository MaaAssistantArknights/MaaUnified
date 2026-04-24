using System.Collections.Generic;

namespace MAAUnified.App.Controls;

public static class StickyTitleMath
{
    public static double ComputeSectionTargetOffset(double headerContentTop, double activationLineY)
    {
        return Math.Max(0d, headerContentTop - activationLineY);
    }

    public static int ResolveActiveSectionIndex(double offsetY, double activationLineY, IReadOnlyList<double> headerContentTops)
    {
        var activationContentY = offsetY + activationLineY;
        var selectedIndex = -1;
        for (var i = 0; i < headerContentTops.Count; i++)
        {
            if (activationContentY < headerContentTops[i])
            {
                break;
            }

            selectedIndex = i;
        }

        return selectedIndex;
    }

    public static int ResolvePinnedHeaderIndex(IReadOnlyList<double> headerViewportTops, double revealLineY)
    {
        var pinnedIndex = -1;
        for (var i = 0; i < headerViewportTops.Count; i++)
        {
            if (headerViewportTops[i] > revealLineY)
            {
                break;
            }

            pinnedIndex = i;
        }

        return pinnedIndex;
    }

    public static double ComputePushOffset(double nextViewportTop, double stickyHeight)
    {
        if (stickyHeight <= 0d)
        {
            return 0d;
        }

        return Math.Clamp(stickyHeight - nextViewportTop, 0d, stickyHeight);
    }
}
