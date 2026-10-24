using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ApiClient? _api;

    private string _searchText = "";
    private string? _selectedGroup;
    private string _filter = "all";
    private string _configurationName = "—";
    private string _lastUpdated = "—";
    private string? _errorMessage;
    private bool _isBusy;
    private int _totalCount;
    private int _onlineCount;
    private int _offlineCount;
    private string _sortMember = "IsOnline";
    private bool _sortDescending = true;
    private readonly HashSet<string> _favoritePeerIds = new(StringComparer.Ordinal);
    private bool _favoritesOnly;
    private int _favoriteCount;
    private string _currentUsername = "—";
    private bool _isAdmin = true;
    private string? _restrictedGroup;

    public ObservableCollection<Peer> Peers { get; } = [];
    public AwgDataState AwgState { get; } = new();
    public void MarkAwgDataStale() => AwgState.IsCurrent = false;
    public ObservableCollection<GroupListItem> Groups { get; } = [];
    public ICollectionView PeersView { get; }

    public MainViewModel()
    {
        PeersView = CollectionViewSource.GetDefaultView(Peers);
        PeersView.Filter = FilterPeer;
        ApplySort(_sortMember, _sortDescending);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value ?? ""))
                PeersView.Refresh();
        }
    }

    public string? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetField(ref _selectedGroup, value))
                PeersView.Refresh();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            var normalized = value is "online" or "offline" ? value : "all";
            if (SetField(ref _filter, normalized))
                PeersView.Refresh();
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetField(ref _favoritesOnly, value))
                PeersView.Refresh();
        }
    }

    public int FavoriteCount
    {
        get => _favoriteCount;
        private set => SetField(ref _favoriteCount, value);
    }

    public string ConfigurationName
    {
        get => _configurationName;
        private set => SetField(ref _configurationName, value);
    }

    public string CurrentUsername
    {
        get => _currentUsername;
        private set => SetField(ref _currentUsername, value);
    }

    public bool IsAdmin
    {
        get => _isAdmin;
        private set
        {
            if (SetField(ref _isAdmin, value))
                OnPropertyChanged(nameof(IsRestrictedUser));
        }
    }

    public bool IsRestrictedUser => !IsAdmin;

    public string? RestrictedGroup
    {
        get => _restrictedGroup;
        private set => SetField(ref _restrictedGroup, value);
    }

    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetField(ref _lastUpdated, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorVisibility));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public System.Windows.Visibility ErrorVisibility => HasError
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetField(ref _totalCount, value);
    }

    public int OnlineCount
    {
        get => _onlineCount;
        private set => SetField(ref _onlineCount, value);
    }

    public int OfflineCount
    {
        get => _offlineCount;
        private set => SetField(ref _offlineCount, value);
    }

    public string SortMember => _sortMember;
    public bool SortDescending => _sortDescending;

    public void Configure(string serverUrl, string username, string password)
    {
        MarkAwgDataStale();
        _api?.Dispose();
        _api = new ApiClient(serverUrl, username, password);

        // Не оставляем данные предыдущей сессии после смены учетных данных.
        // Это особенно важно при переключении с admin на ограниченного пользователя.
        Peers.Clear();
        Groups.Clear();
        TotalCount = 0;
        OnlineCount = 0;
        OfflineCount = 0;
        FavoriteCount = 0;
        ConfigurationName = "—";
        LastUpdated = "—";
        CurrentUsername = string.IsNullOrWhiteSpace(username) ? "—" : username.Trim();
        IsAdmin = false;
        RestrictedGroup = null;
        SelectedGroup = null;
        ErrorMessage = null;
        PeersView.Refresh();
    }

    public void SetFavoriteIds(IEnumerable<string>? peerIds)
    {
        _favoritePeerIds.Clear();
        if (peerIds is not null)
        {
            foreach (var id in peerIds.Where(x => !string.IsNullOrWhiteSpace(x)))
                _favoritePeerIds.Add(id);
        }

        foreach (var peer in Peers)
            peer.IsFavorite = _favoritePeerIds.Contains(peer.Id);

        FavoriteCount = Peers.Count(p => p.IsFavorite);
        PeersView.Refresh();
    }

    public bool IsFavorite(string? peerId) =>
        !string.IsNullOrWhiteSpace(peerId) && _favoritePeerIds.Contains(peerId);

    public void SetFavorite(Peer peer, bool favorite)
    {
        if (string.IsNullOrWhiteSpace(peer.Id))
            return;

        if (favorite)
            _favoritePeerIds.Add(peer.Id);
        else
            _favoritePeerIds.Remove(peer.Id);

        peer.IsFavorite = favorite;
        FavoriteCount = Peers.Count(p => p.IsFavorite);
        PeersView.Refresh();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var api = _api ?? throw new InvalidOperationException("API client is not configured.");

        if (!await _refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            IsBusy = true;

            var meTask = api.GetMeAsync(cancellationToken);
            var peersTask = api.GetPeersAsync(cancellationToken);
            var groupsTask = api.GetGroupsAsync(cancellationToken);
            await Task.WhenAll(meTask, peersTask, groupsTask);
            if (!ReferenceEquals(api, _api)) return;

            var meResponse = await meTask;
            var peerResponse = await peersTask;
            var groupResponse = await groupsTask;

            CurrentUsername = string.IsNullOrWhiteSpace(meResponse.Username)
                ? "—"
                : meResponse.Username;
            IsAdmin = meResponse.IsAdmin;
            RestrictedGroup = meResponse.IsAdmin
                ? null
                : (string.IsNullOrWhiteSpace(meResponse.Group) ? meResponse.Username : meResponse.Group);

            // Для ограниченного пользователя сервер уже возвращает только разрешенную группу.
            // Держим тот же scope и в UI, чтобы не показывать ложный режим "Все клиенты".
            if (IsRestrictedUser && !string.IsNullOrWhiteSpace(RestrictedGroup))
                SelectedGroup = RestrictedGroup;

            var newPeers = peerResponse.Items;

            foreach (var peer in newPeers)
            {
                peer.AwgState = AwgState;
                peer.IsFavorite = _favoritePeerIds.Contains(peer.Id);
            }

            Peers.Clear();
            foreach (var peer in newPeers)
                Peers.Add(peer);

            FavoriteCount = newPeers.Count(p => p.IsFavorite);

            Groups.Clear();
            foreach (var group in groupResponse.Items
                         .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var groupPeerIds = group.PeerIds.ToHashSet(StringComparer.Ordinal);
                var total = newPeers.Count(p => groupPeerIds.Contains(p.Id));
                var online = newPeers.Count(p => groupPeerIds.Contains(p.Id) && p.IsOnline);

                Groups.Add(new GroupListItem
                {
                    Name = group.Name,
                    Total = total,
                    Online = online
                });
            }

            if (!string.IsNullOrWhiteSpace(SelectedGroup) &&
                !Groups.Any(g => string.Equals(g.Name, SelectedGroup, StringComparison.CurrentCulture)))
            {
                SelectedGroup = IsRestrictedUser ? RestrictedGroup : null;
            }

            if (IsRestrictedUser &&
                !FavoritesOnly &&
                !string.IsNullOrWhiteSpace(RestrictedGroup))
            {
                SelectedGroup = RestrictedGroup;
            }

            TotalCount = newPeers.Count;
            OnlineCount = newPeers.Count(p => p.IsOnline);
            OfflineCount = TotalCount - OnlineCount;
            ConfigurationName = peerResponse.Configuration ?? groupResponse.Configuration ?? "—";
            LastUpdated = FormatUpdated(peerResponse.Updated ?? groupResponse.Updated);
            ErrorMessage = null;
            AwgState.IsCurrent = true;

            PeersView.Refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Явная отмена обновления не является ошибкой связи.
        }
        catch (OperationCanceledException ex)
        {
            if (!ReferenceEquals(api, _api)) return;
            MarkAwgDataStale();
            // HttpClient.Timeout тоже выбрасывает OperationCanceledException.
            // Не выпускаем его в async void обработчик таймера WPF.
            ErrorMessage = "Сервер не ответил за 12 секунд. Проверьте VPN-туннель. Подключение будет повторено автоматически.";
            App.Log("Refresh timed out; keeping last successful data and retrying on next refresh.", ex);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(api, _api)) return;
            MarkAwgDataStale();
            // Не очищаем последний успешно полученный список.
            ErrorMessage = ex is System.Net.Http.HttpRequestException { StatusCode: null }
                ? "Сервер недоступен. Проверьте VPN-туннель. Подключение будет повторено автоматически."
                : ex.Message;
            App.Log("Refresh failed; keeping last successful data and retrying on next refresh.", ex);
        }
        finally
        {
            IsBusy = false;
            _refreshGate.Release();
        }
    }

    public void ApplySort(string member, bool descending)
    {
        _sortMember = member;
        _sortDescending = descending;

        if (PeersView is ListCollectionView listView)
        {
            listView.CustomSort = new PeerComparer(member, descending);
            listView.Refresh();
        }

        OnPropertyChanged(nameof(SortMember));
        OnPropertyChanged(nameof(SortDescending));
    }

    private bool FilterPeer(object item)
    {
        if (item is not Peer peer)
            return false;

        if (Filter == "online" && !peer.IsOnline)
            return false;

        if (Filter == "offline" && peer.IsOnline)
            return false;

        if (FavoritesOnly && !peer.IsFavorite)
            return false;

        if (!string.IsNullOrWhiteSpace(SelectedGroup) &&
            !peer.Groups.Contains(SelectedGroup, StringComparer.CurrentCulture))
            return false;

        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var words = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.All(search =>
               Contains(peer.Name, search) ||
               Contains(peer.VpnIp, search) ||
               Contains(peer.AllowedIp, search) ||
               Contains(peer.Endpoint, search) ||
               Contains(peer.Notes, search) ||
               peer.Groups.Any(g => Contains(g, search)));
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private static string FormatUpdated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "—";

        return DateTime.TryParse(value, out var dt)
            ? dt.ToString("dd.MM.yyyy HH:mm:ss")
            : value;
    }

    private sealed class PeerComparer(string member, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not Peer a) return 1;
            if (y is not Peer b) return -1;

            // Favorites are always pinned above regular peers, regardless of column sort direction.
            var favoriteResult = b.IsFavorite.CompareTo(a.IsFavorite);
            if (favoriteResult != 0)
                return favoriteResult;

            var result = CompareCore(a, b, member);
            if (result == 0)
                result = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);

            return descending ? -result : result;
        }

        private static int CompareCore(Peer a, Peer b, string member)
        {
            return member switch
            {
                "IsOnline" => a.IsOnline.CompareTo(b.IsOnline),
                "Name" => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                "VpnIpSortKey" => a.VpnIpSortKey.CompareTo(b.VpnIpSortKey),
                "HandshakeSortKey" => CompareHandshake(a, b),
                "TrafficTotal" => a.TrafficTotal.CompareTo(b.TrafficTotal),
                "GroupsText" => StringComparer.CurrentCultureIgnoreCase.Compare(a.GroupsText, b.GroupsText),
                _ => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)
            };
        }

        private static int CompareHandshake(Peer a, Peer b)
        {
            var aMissing = double.IsPositiveInfinity(a.HandshakeSortKey);
            var bMissing = double.IsPositiveInfinity(b.HandshakeSortKey);

            if (aMissing && !bMissing) return 1;
            if (!aMissing && bMissing) return -1;
            return a.HandshakeSortKey.CompareTo(b.HandshakeSortKey);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        _api?.Dispose();
        _refreshGate.Dispose();
    }
}
