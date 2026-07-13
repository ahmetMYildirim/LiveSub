using System.Collections.ObjectModel;

namespace PsGameTranslator.App.ViewModels.User;

public sealed class ShortcutsPageViewModel : ObservableObject
{
    public ObservableCollection<ShortcutItem> Shortcuts { get; } =
    [
        new("Ctrl + Shift + D", "Geliştirici modunu aç/kapat"),
        new("Başlık çubuğunda çift tıklama", "Pencereyi büyüt/küçült"),
        new("Başlık çubuğunu sürükleme", "Pencereyi taşı"),
        new("Pencere kenarları", "Pencereyi yeniden boyutlandır"),
    ];
}

public sealed record ShortcutItem(string Keys, string Description);
