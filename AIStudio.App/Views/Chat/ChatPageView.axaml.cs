using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AIStudio.App.ViewModels.Chat;

namespace AIStudio.App.Views.Chat;

public partial class ChatPageView : UserControl
{
    private ChatPageViewModel?    _vm;
    private ConversationViewModel? _subscribedConv;
    private bool                   _autoScroll = true;

    /// <summary>
    /// True když právě programmaticky scrollujeme na konec (uvnitř ScrollToBottom retry
    /// okna). OnScrollChanged tehdy NESMÍ přepočítat _autoScroll, protože by ho mohl
    /// chybně vypnout uprostřed sekvence (intermediate layout state kde atBottom != true).
    /// </summary>
    private bool                   _programmaticScroll;

    public ChatPageView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => FocusInputBox();
        Unloaded += OnUnloaded;

        var scroll = this.FindControl<ScrollViewer>("MessagesScroll")!;
        scroll.ScrollChanged += OnScrollChanged;

        // Mouse wheel handler — chceme aby kolečko fungovalo i během streamování
        // bez toho, aby uživatel musel nejdřív kliknout do scrollbaru (a tím
        // ztratit fokus z inputu). Tunneling handler (RoutingStrategies.Tunnel)
        // zachytí wheel event PŘED tím, než ho dostane TextBox v inputu nebo
        // jiný element pod kurzorem — jinak by ho mohl spotřebovat něco jiného.
        //
        // Logika:
        //   • Wheel up (Delta.Y > 0)  = uživatel se posouvá nahoru → vypnout autoScroll
        //   • Wheel down (Delta.Y < 0) = uživatel se posouvá dolů  → necháme OnScrollChanged
        //                                rozhodnout (pokud doscrolloval na konec, znova zapne)
        scroll.AddHandler(PointerWheelChangedEvent, OnChatWheelTunnel,
                          Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Drag & drop obrázků do input oblasti — přetáhni fotky z Průzkumníka /
        // jiného okna a pustí se jako přílohy (multi). Handlery na pojmenovaném
        // Borderu kolem celé input oblasti, ať je drop zóna velká.
        var dropArea = this.FindControl<Border>("InputDropArea");
        if (dropArea is not null)
        {
            dropArea.AddHandler(DragDrop.DragOverEvent, OnInputDragOver);
            dropArea.AddHandler(DragDrop.DropEvent,     OnInputDrop);
        }
    }

    private void OnChatWheelTunnel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        // Pouze pokud je kurzor reálně nad MessagesScroll — jinak nech default routing.
        var scroll = this.FindControl<ScrollViewer>("MessagesScroll");
        if (scroll is null) return;

        // Wheel směrem nahoru = uživatel chce číst minulost → okamžitě vypni autoScroll
        // a přeruš programmatic retry window. Bez tohoto se autoScroll znova zapnul
        // přes ScrollToBottom uvnitř 25-frame retry okna, takže manuální scrollnutí
        // během streamování bylo neúčinné.
        if (e.Delta.Y > 0)
        {
            _programmaticScroll = false;  // zruš probíhající retry okno
            SetAutoScroll(false);
            // Necháme event propagovat — ScrollViewer ho zkonzumuje sám a posune offset
        }
        // Pro wheel dolů necháme normální flow — OnScrollChanged spočte atBottom
        // a případně znovu zapne autoScroll, když se uživatel doscroluje na konec.
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }

        if (_subscribedConv is not null)
        {
            _subscribedConv.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var m in _subscribedConv.Messages)
                m.PropertyChanged -= OnMessagePropertyChanged;
            _subscribedConv = null;
        }
    }

    /// <summary>
    /// Posune fokus do pole pro psaní zprávy. Jinak by uživatel musel napřed
    /// kliknout do textboxu, aby Enter poslal zprávu — první stisk se ztrácel.
    /// Voláno po startu a po každé změně konverzace.
    /// </summary>
    private void FocusInputBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<TextBox>("ChatInputBox")?.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ── DataContext wiring ────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as ChatPageViewModel;

        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        SetAutoScroll(true);
        ScrollToBottom();
        FocusInputBox();
    }

    // ── FAB (scroll-to-bottom) ────────────────────────────────────────────────

    private void SetAutoScroll(bool value)
    {
        _autoScroll = value;
        var fab = this.FindControl<Button>("ScrollToBottomFab");
        if (fab is not null) fab.IsVisible = !value;
    }

    private void ScrollToBottomFab_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetAutoScroll(true);
        ScrollToBottom();
    }

    // ── Conversation switch ───────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatPageViewModel.SelectedConversation)) return;

        // Přepnout CollectionChanged + per-message PropertyChanged odběr na novou konverzaci
        if (_subscribedConv is not null)
        {
            _subscribedConv.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var m in _subscribedConv.Messages)
                m.PropertyChanged -= OnMessagePropertyChanged;
        }

        _subscribedConv = _vm?.SelectedConversation;

        if (_subscribedConv is not null)
        {
            _subscribedConv.Messages.CollectionChanged += OnMessagesChanged;
            // Pokrývá i regenerate/edit existující asistentské zprávy — přechod
            // IsStreaming false→true→false znovu vyvolá auto-scroll
            foreach (var m in _subscribedConv.Messages)
                m.PropertyChanged += OnMessagePropertyChanged;
        }

        SetAutoScroll(true);
        ScrollToBottom();
        FocusInputBox();
    }

    // ── Drag & drop obrázků do inputu (multi) ─────────────────────────────────

    private static readonly System.Collections.Generic.HashSet<string> _dropExtensions =
        new(System.StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    private void OnInputDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is not null && files.Any(f =>
            f.TryGetLocalPath() is { } p && _dropExtensions.Contains(Path.GetExtension(p)))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || _vm is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && _dropExtensions.Contains(Path.GetExtension(p)))
            .Cast<string>();

        _vm.AddAttachments(paths);
        e.Handled = true;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Přihlas se / odhlas se z PropertyChanged jednotlivých zpráv —
        // potřebujeme zachytit přepnutí IsStreaming → false (dokončení odpovědi),
        // kdy se mění výška bubliny (plain text → markdown viewer) a default
        // auto-scroll based on ExtentDelta to nemusí stihnout pokrýt.
        if (e.NewItems is not null)
            foreach (ChatMessage m in e.NewItems)
                m.PropertyChanged += OnMessagePropertyChanged;

        if (e.OldItems is not null)
            foreach (ChatMessage m in e.OldItems)
                m.PropertyChanged -= OnMessagePropertyChanged;

        // Nová zpráva přidána → scrolluj pokud jsme dole
        if (e.Action == NotifyCollectionChangedAction.Add && _autoScroll)
            ScrollToBottom();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Po dokončení streamu (IsStreaming přejde na false) layout přepočítá
        // bublinu s markdown rendererem — explicitně doscrollujeme na konec,
        // jinak by poslední řádky bubliny mohly viset pod viewportem.
        if (e.PropertyName == nameof(ChatMessage.IsStreaming) &&
            sender is ChatMessage { IsStreaming: false } &&
            _autoScroll)
        {
            ScrollToBottom();
        }
    }

    // ── Auto-scroll ───────────────────────────────────────────────────────────

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroll = (ScrollViewer)sender!;

        if (e.ExtentDelta.Y > 0)
        {
            // Obsah se zvětšil (streaming) — scrollovat pokud jsme dole
            if (_autoScroll) ScrollToBottom();
            return;
        }

        if (e.OffsetDelta.Y != 0)
        {
            // Během programmatic scroll (ScrollToBottom retry okno) NESMÍME
            // přepočítávat _autoScroll — intermediate layout stavy mohou hlásit
            // atBottom=false a tím by se vypnul auto-scroll uprostřed sekvence.
            if (_programmaticScroll) return;

            // Uživatel scrolloval — zjisti, jestli je na konci
            // B6: relativní práh — min 40 px nebo 10 % výšky viewportu (pro krátké/vysoké okno)
            var threshold = Math.Max(40, scroll.Viewport.Height * 0.1);
            var atBottom  = scroll.Offset.Y + scroll.Viewport.Height
                            >= scroll.Extent.Height - threshold;
            SetAutoScroll(atBottom);
        }
    }

    private void ScrollToBottom()
    {
        // Princip: po Post na DispatcherPriority.Render proběhne layout
        // pass synchronně, takže Extent je aktuální. Nastavíme Offset
        // na Extent.Height (ScrollViewer clampuje na max valid).
        //
        // Následně sledujeme LayoutUpdated — když se po prvním nastavení
        // změní layout (streaming, MarkdownViewer render, footer becomes
        // visible), znovu doscrollujeme. POZOR: v LayoutUpdated handleru
        // NESMÍME volat UpdateLayout — to by vyvolalo nový layout pass
        // uvnitř probíhajícího, který by zavolal LayoutUpdated, který
        // by zavolal UpdateLayout… nekonečná rekurze → StackOverflow.
        // Stačí nastavit Offset — clamp proběhne automaticky.

        var scroll = this.FindControl<ScrollViewer>("MessagesScroll");
        if (scroll is null) return;

        _programmaticScroll = true;

        Dispatcher.UIThread.Post(() =>
        {
            // Initial scroll — vynutíme layout pass jen tady (mimo LayoutUpdated)
            scroll.UpdateLayout();
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);

            var framesSeen = 0;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                framesSeen++;
                // POZOR: žádné UpdateLayout() tady — handler běží UVNITŘ layout passu,
                // další UpdateLayout by vyvolal rekurzi.
                scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
                if (framesSeen >= 25)
                {
                    scroll.LayoutUpdated -= handler!;
                    _programmaticScroll = false;
                }
            };
            scroll.LayoutUpdated += handler;
        }, DispatcherPriority.Render);
    }

    // ── Generated image click → zoom ──────────────────────────────────────────

    /// <summary>
    /// Klik (nebo tap) na vygenerovaný obrázek v chat bublině → otevře zoom okno
    /// přes ChatMessage.OpenImageZoomCommand. Předtím to bylo realizováno
    /// Buttonem s Command bindingem, ale ten v některých situacích nefungoval
    /// — uživatel hlásil, že klik místo zoomu jakoby přidal obrázek do referenčních
    /// (zřejmě se Button click nevyhodnotil, místo toho event probublal jinam
    /// nebo se nezavolal ICommand kvůli ne-deterministickému DataContextu).
    /// Tapped event handler v code-behind řeší to explicitně bez bindings.
    /// </summary>
    private void GeneratedImage_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control ctrl) return;

        if (ctrl.DataContext is ChatMessage msg && msg.OpenImageZoomCommand.CanExecute(null))
        {
            msg.OpenImageZoomCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Title inline edit ─────────────────────────────────────────────────────

    private void TitleEditBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ChatPageViewModel vm)
            vm.ConfirmTitleEditCommand.Execute(null);
    }

    private void TitleEditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.ConfirmTitleEditCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelTitleEditCommand.Execute(null);
        }
    }

    // ── Chat input ────────────────────────────────────────────────────────────

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        // Enter (bez Shift) nebo Ctrl+Enter → odeslat zprávu
        var isEnter     = e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0;
        var isCtrlEnter = e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) != 0;

        if (isEnter || isCtrlEnter)
        {
            e.Handled = true;
            vm.SendMessageCommand.Execute(null);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (vm.IsSending)
                vm.StopGenerationCommand.Execute(null);
            // Pokud probíhá edit zprávy, zruší ho první Esc
            else if (vm.SelectedConversation?.Messages
                         .FirstOrDefault(m => m.IsEditing) is { } editing)
                editing.CancelEditCommand.Execute(null);
        }
    }
}
