using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Models;

/// <summary>
/// Jedna sekce v tabu „Doporučené" — odpovídá jednomu <see cref="ModelPick"/>.
/// UI ji vykreslí jako akordion s nadpisem (<see cref="Title"/>), popiskem
/// (<see cref="Hint"/>) a seznamem živě načtených modelů (<see cref="Models"/>).
///
/// Načítání je odložené (lazy) — discovery service je volaná až když uživatel
/// poprvé otevře tab „Doporučené". <see cref="IsLoading"/> drží spinner během fetche
/// a <see cref="LoadError"/> ukáže chybu, když API selže (typicky no-internet).
/// </summary>
public partial class RecommendedSectionViewModel : ObservableObject
{
    public string   Id    { get; }
    public string   Title { get; }
    public string   Hint  { get; }
    public PickKind Kind  { get; }
    public ModelPick Pick { get; }

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _loadError = string.Empty;
    [ObservableProperty] private bool   _hasLoaded;

    /// <summary>Modely v sekci — naplní se po prvním fetch z discovery service.</summary>
    public ObservableCollection<ModelItemViewModel> Models { get; } = new();

    /// <summary>True když má smysl ukázat empty state („Nic nenalezeno") — load proběhl, ale nic nepřišlo.</summary>
    public bool ShowEmptyState => HasLoaded && !IsLoading && Models.Count == 0 && string.IsNullOrEmpty(LoadError);

    public RecommendedSectionViewModel(ModelPick pick)
    {
        Pick  = pick;
        Id    = pick.Id;
        Title = pick.Title;
        Hint  = pick.Hint;
        Kind  = pick.Kind;
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));
    partial void OnHasLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));
    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(ShowEmptyState));
}
