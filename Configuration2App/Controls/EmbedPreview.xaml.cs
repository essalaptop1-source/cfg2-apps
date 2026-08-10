using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Configuration2App.Models;

namespace Configuration2App.Controls;

/// <summary>
/// Reusable component that renders an <see cref="EmbedDraft"/> the way Discord displays
/// embeds: dark card, colored accent bar, author/title/description/fields/image/footer.
/// </summary>
public partial class EmbedPreview : UserControl
{
    private static readonly Dictionary<string, BitmapImage> ImageCache = new();

    public EmbedPreview()
    {
        InitializeComponent();
    }

    public void Show(EmbedDraft draft)
    {
        AccentBar.Background = new SolidColorBrush(IntToColor(EmbedDraft.ParseColor(draft.ColorHex) ?? 0x5865F2));

        // Author
        var hasAuthor = !string.IsNullOrWhiteSpace(draft.AuthorName);
        AuthorRow.Visibility = hasAuthor ? Visibility.Visible : Visibility.Collapsed;
        AuthorNameText.Text = draft.AuthorName;
        if (!string.IsNullOrWhiteSpace(draft.AuthorIcon))
        {
            AuthorIconCircle.Visibility = Visibility.Visible;
            TrySetImage(AuthorIconImage, draft.AuthorIcon, 36);
        }
        else
        {
            AuthorIconCircle.Visibility = Visibility.Collapsed;
            AuthorIconImage.Source = null;
        }

        // Title
        TitleText.Text = draft.Title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(draft.Title)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Description
        DescText.Text = draft.Description;
        DescText.Visibility = string.IsNullOrWhiteSpace(draft.Description)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Fields — non-inline fields span the full width; inline ones sit side by side.
        FieldsGrid.Children.Clear();
        FieldsGrid.RowDefinitions.Clear();
        FieldsGrid.ColumnDefinitions.Clear();
        FieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fields = draft.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrWhiteSpace(f.Value))
            .ToList();
        if (fields.Count > 0)
        {
            FieldsGrid.Visibility = Visibility.Visible;
            var row = 0;
            var col = 0;
            foreach (var field in fields)
            {
                FieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
                cell.Children.Add(new TextBlock
                {
                    Text = field.Name,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xDB, 0xDE, 0xE1)),
                    TextWrapping = TextWrapping.Wrap,
                });
                cell.Children.Add(new TextBlock
                {
                    Text = field.Value,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xDB, 0xDE, 0xE1)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });

                Grid.SetRow(cell, row);
                if (field.IsInline)
                {
                    Grid.SetColumn(cell, col);
                    col++;
                    if (col == 2) { col = 0; row++; }
                }
                else
                {
                    Grid.SetColumn(cell, 0);
                    Grid.SetColumnSpan(cell, 2);
                    row++;
                    col = 0;
                }
                FieldsGrid.Children.Add(cell);
            }
        }
        else
        {
            FieldsGrid.Visibility = Visibility.Collapsed;
        }

        // Large image
        if (!string.IsNullOrWhiteSpace(draft.ImageUrl))
        {
            ImagePreview.Visibility = Visibility.Visible;
            TrySetImage(ImagePreview, draft.ImageUrl, 480);
        }
        else
        {
            ImagePreview.Visibility = Visibility.Collapsed;
            ImagePreview.Source = null;
        }

        // Footer
        var hasFooter = !string.IsNullOrWhiteSpace(draft.FooterText) ||
                        !string.IsNullOrWhiteSpace(draft.FooterIcon) ||
                        draft.IncludeTimestamp;
        FooterRow.Visibility = hasFooter ? Visibility.Visible : Visibility.Collapsed;

        var footerParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(draft.FooterText)) footerParts.Add(draft.FooterText);
        if (draft.IncludeTimestamp) footerParts.Add(DateTime.Now.ToString("MMM d, yyyy h:mm tt"));
        FooterText.Text = string.Join(" · ", footerParts);

        if (!string.IsNullOrWhiteSpace(draft.FooterIcon))
        {
            FooterIconImage.Visibility = Visibility.Visible;
            TrySetImage(FooterIconImage, draft.FooterIcon, 32);
            FooterText.Margin = new Thickness(6, 0, 0, 0);
        }
        else
        {
            FooterIconImage.Visibility = Visibility.Collapsed;
            FooterIconImage.Source = null;
            FooterText.Margin = new Thickness(0, 0, 0, 0);
        }
    }

    public void Clear() => Show(new EmbedDraft());

    private static Color IntToColor(int value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static void TrySetImage(Image image, string url, int decodeWidth)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                image.Source = null;
                return;
            }

            if (!ImageCache.TryGetValue(url, out var bitmap))
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                bitmap.EndInit();
                bitmap.Freeze();
                ImageCache[url] = bitmap;
            }
            image.Source = bitmap;
        }
        catch
        {
            image.Source = null;
        }
    }
}
