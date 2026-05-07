using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class AdaptiveSpacingStackPanelTests
{
    [Fact]
    public void SupplementaryText_ShouldKeepPositiveGapFromPreviousRow()
    {
        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 48,
            MinGap = 8,
            MaxGap = 16,
            ShortRowSlot = 28,
        };

        var input = new FixedTextBox(160, 38);
        var note = new FixedTextBlock(160, 16, "note", "settings-note");

        panel.Children.Add(input);
        panel.Children.Add(note);

        Layout(panel, 420);

        var gap = note.Bounds.Top - input.Bounds.Bottom;
        Assert.InRange(gap, 4, 6);
    }

    [Fact]
    public void NestedFieldGroups_ShouldKeepCaptionFieldGapTighterThanInterGroupGap()
    {
        var outer = new AdaptiveSpacingStackPanel
        {
            Spacing = 16,
            TargetPitch = 50,
            MinGap = 10,
            MaxGap = 18,
            ShortRowSlot = 28,
        };

        var firstGroup = CreateFieldGroup();
        var secondGroup = CreateFieldGroup();
        outer.Children.Add(firstGroup);
        outer.Children.Add(secondGroup);

        Layout(outer, 420);

        var firstCaption = (FixedTextBlock)firstGroup.Children[0];
        var firstInput = (TextBox)firstGroup.Children[1];
        var innerGap = firstInput.Bounds.Top - firstCaption.Bounds.Bottom;
        var interGroupGap = secondGroup.Bounds.Top - firstGroup.Bounds.Bottom;

        Assert.InRange(innerGap, 4, 8);
        Assert.True(interGroupGap >= 10, "Field groups should keep a readable gap between each other.");
        Assert.True(
            interGroupGap > innerGap,
            $"Expected the gap between groups ({interGroupGap}) to exceed the caption-to-field gap ({innerGap}).");
    }

    [Fact]
    public void CaptionNoteAfterToggle_ShouldUseSupplementaryGap()
    {
        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 48,
            MinGap = 6,
            MaxGap = 18,
            ShortRowSlot = 20,
        };

        var toggle = new FixedCheckBox(160, 20, "A");
        var note = new FixedTextBlock(160, 16, "note", "app-caption");

        panel.Children.Add(toggle);
        panel.Children.Add(note);

        Layout(panel, 420);

        var gap = note.Bounds.Top - toggle.Bounds.Bottom;

        Assert.InRange(gap, 4, 6);
    }

    [Fact]
    public void CaptionNoteAfterToggle_ShouldHonorExplicitSupplementaryGap()
    {
        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 48,
            MinGap = 6,
            MaxGap = 18,
            ShortRowSlot = 20,
            SupplementaryTextGap = 2,
        };

        var toggle = new FixedCheckBox(160, 20, "A");
        var note = new FixedTextBlock(160, 16, "note", "app-caption", "settings-note", "start-settings-note");

        panel.Children.Add(toggle);
        panel.Children.Add(note);

        Layout(panel, 420);

        var gap = note.Bounds.Top - toggle.Bounds.Bottom;

        Assert.Equal(2, gap);
    }

    [Fact]
    public void CaptionNoteAfterToggle_ShouldHonorNegativeExplicitSupplementaryGap()
    {
        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 48,
            MinGap = 6,
            MaxGap = 18,
            ShortRowSlot = 20,
            SupplementaryTextGap = -6,
        };

        var toggle = new FixedCheckBox(160, 20, "A");
        var note = new FixedTextBlock(160, 16, "note", "app-caption", "settings-note", "start-settings-note");

        panel.Children.Add(toggle);
        panel.Children.Add(note);

        Layout(panel, 420);

        var gap = note.Bounds.Top - toggle.Bounds.Bottom;

        Assert.Equal(-6, gap);
    }

    [Fact]
    public void SupplementaryTextBeforeToggle_ShouldHonorExplicitTrailingGap()
    {
        var firstGroup = new StackPanel
        {
            Spacing = 0,
        };
        firstGroup.Children.Add(new FixedCheckBox(160, 20, "A"));
        firstGroup.Children.Add(new FixedTextBlock(160, 16, "note", "app-caption", "settings-note", "start-settings-note"));

        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 46,
            MinGap = 6,
            MaxGap = 18,
            ShortRowSlot = 20,
            AfterSupplementaryTextGap = -2,
        };

        var secondToggle = new FixedCheckBox(160, 20, "B");
        panel.Children.Add(firstGroup);
        panel.Children.Add(secondToggle);

        Layout(panel, 420);

        var gap = secondToggle.Bounds.Top - firstGroup.Bounds.Bottom;

        Assert.Equal(-2, gap);
    }

    [Fact]
    public void CaptionDividerSectionFlow_ShouldAvoidDoubleDefaultGapAroundDivider()
    {
        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 48,
            MinGap = 6,
            MaxGap = 18,
            ShortRowSlot = 20,
        };

        var note = new FixedTextBlock(160, 16, "note", "app-caption");
        var divider = new FixedBorder(160, 1, "settings-page-subtle-divider");
        var section = new FixedTextBlock(160, 20, "section", "app-body");

        panel.Children.Add(note);
        panel.Children.Add(divider);
        panel.Children.Add(section);

        Layout(panel, 420);

        var captionToDividerGap = divider.Bounds.Top - note.Bounds.Bottom;
        var dividerToSectionGap = section.Bounds.Top - divider.Bounds.Bottom;

        Assert.InRange(captionToDividerGap, 4, 6);
        Assert.InRange(dividerToSectionGap, 4, 6);
    }

    [Fact]
    public void MixedToggleAndFieldPairs_ShouldStayBetweenToggleAndFieldRhythms()
    {
        var panel = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 52,
            MinGap = 6,
            MaxGap = 18,
            ShortRowSlot = 20,
        };

        var firstCheck = new FixedCheckBox(160, 20, "A");
        var secondCheck = new FixedCheckBox(160, 20, "B");
        var firstInput = new FixedTextBox(160, 38);
        var secondInput = new FixedTextBox(160, 38);
        var thirdCheck = new FixedCheckBox(160, 20, "C");

        panel.Children.Add(firstCheck);
        panel.Children.Add(secondCheck);
        panel.Children.Add(firstInput);
        panel.Children.Add(secondInput);
        panel.Children.Add(thirdCheck);

        Layout(panel, 420);

        var toggleGap = secondCheck.Bounds.Top - firstCheck.Bounds.Bottom;
        var mixedForwardGap = firstInput.Bounds.Top - secondCheck.Bounds.Bottom;
        var fieldGap = secondInput.Bounds.Top - firstInput.Bounds.Bottom;
        var mixedBackwardGap = thirdCheck.Bounds.Top - secondInput.Bounds.Bottom;

        Assert.InRange(toggleGap, 6, 10);
        Assert.True(
            mixedForwardGap > toggleGap,
            $"Expected checkbox/input gap ({mixedForwardGap}) to exceed checkbox/checkbox gap ({toggleGap}).");
        Assert.True(
            mixedBackwardGap > toggleGap,
            $"Expected input/checkbox gap ({mixedBackwardGap}) to exceed checkbox/checkbox gap ({toggleGap}).");
        Assert.True(
            mixedForwardGap < fieldGap,
            $"Expected checkbox/input gap ({mixedForwardGap}) to stay tighter than input/input gap ({fieldGap}).");
        Assert.True(
            mixedBackwardGap < fieldGap,
            $"Expected input/checkbox gap ({mixedBackwardGap}) to stay tighter than input/input gap ({fieldGap}).");
    }

    private static AdaptiveSpacingStackPanel CreateFieldGroup()
    {
        var group = new AdaptiveSpacingStackPanel
        {
            Spacing = 6,
            TargetPitch = 50,
            MinGap = 10,
            MaxGap = 18,
            ShortRowSlot = 28,
        };

        var caption = new FixedTextBlock(160, 16, "caption", "app-caption");
        group.Children.Add(caption);
        group.Children.Add(new FixedTextBox(160, 38));

        return group;
    }

    private static void Layout(Control control, double width)
    {
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
    }

    private sealed class FixedTextBlock : TextBlock
    {
        private readonly double _width;
        private readonly double _height;

        public FixedTextBlock(double width, double height, string text, params string[] classes)
        {
            _width = width;
            _height = height;
            foreach (var className in classes)
            {
                Classes.Add(className);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(_width, _height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            return new Size(_width, _height);
        }

        protected override void ArrangeCore(Rect finalRect)
        {
            Bounds = finalRect;
        }
    }

    private sealed class FixedTextBox : TextBox
    {
        private readonly double _width;
        private readonly double _height;

        public FixedTextBox(double width, double height)
        {
            _width = width;
            _height = height;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(_width, _height);
        }
    }

    private sealed class FixedCheckBox : CheckBox
    {
        private readonly double _width;
        private readonly double _height;

        public FixedCheckBox(double width, double height, string contentText)
        {
            _width = width;
            _height = height;
            Content = contentText;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(_width, _height);
        }
    }

    private sealed class FixedBorder : Border
    {
        private readonly double _width;
        private readonly double _height;

        public FixedBorder(double width, double height, params string[] classes)
        {
            _width = width;
            _height = height;
            foreach (var className in classes)
            {
                Classes.Add(className);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(_width, _height);
        }
    }
}
