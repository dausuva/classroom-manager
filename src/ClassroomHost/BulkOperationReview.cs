// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomManager.Host;

public sealed record BulkOperationReviewItem(string Label, string Value);

public sealed record BulkOperationReview(
    string Heading,
    string Target,
    IReadOnlyList<BulkOperationReviewItem> Items,
    string Guidance = "",
    string Warning = "",
    string RunLabel = "Run")
{
    public string FormatSummary()
    {
        var text = new StringBuilder()
            .AppendLine(Heading)
            .AppendLine($"Target: {Target}");
        foreach (var item in Items) text.AppendLine($"{item.Label}: {item.Value}");
        if (Guidance.Length > 0) text.AppendLine($"Note: {Guidance}");
        if (Warning.Length > 0) text.AppendLine($"Warning: {Warning}");
        return text.ToString().TrimEnd();
    }
}

public enum BulkOperationReviewDecision
{
    Cancel,
    Back,
    Run,
}

public sealed class BulkOperationReviewWindow : Window
{
    BulkOperationReviewDecision _decision;

    public BulkOperationReviewWindow(Window owner, BulkOperationReview review)
    {
        Owner = owner;
        Title = review.Heading + " — Review";
        Width = 610;
        MinWidth = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height * 0.85);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock
        {
            Text = "1 Select  ✓     2 Configure  ✓     3 Review     4 Run     5 Results",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        content.Children.Add(new TextBlock
        {
            Text = review.Heading,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var target = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = "Target\n" + review.Target, TextWrapping = TextWrapping.Wrap },
        };
        target.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        target.SetResourceReference(Border.BackgroundProperty, "HeaderBackgroundBrush");
        content.Children.Add(target);

        foreach (var item in review.Items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = item.Label, FontWeight = FontWeights.SemiBold });
            var value = new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            content.Children.Add(row);
        }

        if (review.Guidance.Length > 0)
            content.Children.Add(Ui.MutedText(review.Guidance, new Thickness(0, 5, 0, 10)));
        if (review.Warning.Length > 0)
        {
            var warning = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 2, 0, 12),
                Child = new TextBlock
                {
                    Text = "Attention\n" + review.Warning,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            warning.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            content.Children.Add(warning);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var back = new Button { Content = "_Back", MinWidth = 90 };
        back.Click += (_, _) => { _decision = BulkOperationReviewDecision.Back; Close(); };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var run = new Button
        {
            Content = "_" + review.RunLabel,
            MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = review.Warning.Length == 0,
        };
        run.Click += (_, _) => { _decision = BulkOperationReviewDecision.Run; DialogResult = true; };
        buttons.Children.Add(back);
        buttons.Children.Add(cancel);
        buttons.Children.Add(run);
        content.Children.Add(buttons);

        Content = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    public BulkOperationReviewDecision Ask()
    {
        ShowDialog();
        return _decision;
    }
}
