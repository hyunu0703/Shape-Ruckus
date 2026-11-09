using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Shared 로비와 방 연결 수명 및 네트워크 콜백 관리
/// </summary>
public sealed class SharedSession : MonoBehaviour, INetworkRunnerCallbacks
{
    public const int MaxPlayers = 10;
    public const string RoomKind = "shape-5v5-v1";
    public const float RefreshInterval = 5f;
    public enum Screen { Start, Lobby, Room, Loading, Game }

    [SerializeField] private PlayerRoomState _playerPrefab;
    [SerializeField] private string _menuScenePath = "Assets/Scenes/MainMenu.unity";
    [SerializeField] private string _gameScenePath = "Assets/Scenes/GameTest.unity";
    private readonly Dictionary<PlayerRef, PlayerRoomState> _players = new Dictionary<PlayerRef, PlayerRoomState>(MaxPlayers);
    private readonly List<SessionInfo> _sessions = new List<SessionInfo>();
    private readonly List<SessionInfo>[] _groups = new List<SessionInfo>[MaxPlayers + 1];
    private NetworkRunner _runner;
    private RoomGameState _roomState;
    private bool _isBusy;
    private bool _isJoiningLobby;
    private bool _hasSessionList;
    private bool _isDestroyed;
    private float _lastRefresh = float.NegativeInfinity;

    public static SharedSession Instance { get; private set; }
    public NetworkRunner Runner => _runner;
    public Screen CurrentScreen { get; private set; }
    public string Status { get; private set; } = "온라인 플레이를 눌러 시작하세요.";
    public string RoomError { get; private set; } = "";
    public bool IsBusy => _isBusy;
    public bool IsSceneLoading { get; private set; }
    public bool HasSessionList => _hasSessionList;
    public float RefreshRemaining => Mathf.Max(0f, RefreshInterval - (Time.realtimeSinceStartup - _lastRefresh));
    public IReadOnlyList<SessionInfo> Sessions => _sessions;
    public IReadOnlyDictionary<PlayerRef, PlayerRoomState> Players => _players;
    public RoomGameState RoomState => _roomState;
    public string RoomTitle => _runner != null ? GetTitle(_runner.SessionInfo) : "";
    public event Action Changed;
    public event Action SessionListChanged;

    // 영속 관리 객체 초기화 및 중복 Scene 인스턴스 정리
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        for (int i = 0; i <= MaxPlayers; i++) { _groups[i] = new List<SessionInfo>(); }
        if (_playerPrefab == null)
        {
            Debug.LogError("SharedSession의 PlayerRoomState Prefab 누락", this);
            enabled = false;
        }
    }

    // 파괴된 UI와 이전 Runner로 향하는 콜백 차단
    private void OnDestroy()
    {
        _isDestroyed = true;
        if (_runner != null) { _runner.RemoveCallbacks(this); }
        if (Instance == this) { Instance = null; }
        Changed = null;
        SessionListChanged = null;
    }

    // 온라인 버튼 입력에 따른 최초 로비 연결
    public async Task ConnectAsync()
    {
        if (!BeginOperation(Screen.Start)) { return; }
        try { await JoinLobbyAsync(); }
        catch (Exception exception) { await HandleConnectionErrorAsync(exception); }
        finally { EndOperation(); }
    }

    // 새 Runner를 사용하는 로비 새로고침 및 실패 후 재시도
    public async Task RefreshAsync()
    {
        if (RefreshRemaining > 0f || !BeginOperation(Screen.Lobby)) { return; }
        _lastRefresh = Time.realtimeSinceStartup;
        try
        {
            await StopRunnerAsync();
            await JoinLobbyAsync();
        }
        catch (Exception exception) { await HandleConnectionErrorAsync(exception); }
        finally { EndOperation(); }
    }

    // 표시 이름 검증 후 고유 세션 생성
    public async Task CreateRoomAsync(string input)
    {
        if (_isBusy || CurrentScreen != Screen.Lobby) { return; }
        if (!TryValidateTitle(input, out string title, out string error))
        {
            RoomError = error;
            NotifyChanged();
            return;
        }
        await EnterRoomAsync("sr-" + Guid.NewGuid().ToString("N"), title, true);
    }

    // 최신 목록에 남아 있는 참가 가능 방만 참가 요청
    public async Task JoinRoomAsync(string sessionName)
    {
        if (_isBusy || CurrentScreen != Screen.Lobby) { return; }
        foreach (SessionInfo session in _sessions)
        {
            if (session.Name == sessionName && IsJoinable(session))
            {
                await EnterRoomAsync(sessionName, null, false);
                return;
            }
        }
        Status = "방에 참가할 수 없습니다.";
        NotifyChanged();
    }

    // 로비 연결 종료 후 시작 화면 복원
    public async Task BackAsync()
    {
        if (!BeginOperation(Screen.Lobby)) { return; }
        try
        {
            await StopRunnerAsync();
            CurrentScreen = Screen.Start;
            Status = "온라인 플레이를 눌러 시작하세요.";
            RoomError = "";
        }
        catch (Exception exception) { await HandleConnectionErrorAsync(exception); }
        finally { EndOperation(); }
    }

    // 방 퇴장 후 새 Runner로 Shared 로비 재접속
    public async Task LeaveRoomAsync()
    {
        if (!BeginOperation(Screen.Room)) { return; }
        try
        {
            await StopRunnerAsync();
            CurrentScreen = Screen.Lobby;
            await JoinLobbyAsync();
        }
        catch (Exception exception) { await HandleConnectionErrorAsync(exception); }
        finally { EndOperation(); }
    }

    // 빌드 환경 종료 전 네트워크 연결 정리
    public async Task QuitAsync()
    {
#if !UNITY_EDITOR
        if (_isBusy) { return; }
        _isBusy = true;
        NotifyChanged();
        try { await StopRunnerAsync(); }
        catch (Exception exception) { Debug.LogException(exception, this); }
        Application.Quit();
#else
        await Task.CompletedTask;
#endif
    }

    // 로컬 State Authority 플레이어의 준비 상태 전환
    public void ToggleReady()
    {
        if (_isBusy || IsSceneLoading || CurrentScreen != Screen.Room || _runner == null) { return; }
        if (_players.TryGetValue(_runner.LocalPlayer, out PlayerRoomState player) && player != null) { player.ToggleReady(); }
    }

    // 네트워크 방 상태 객체로 시작 요청 전달
    public void StartMatch()
    {
        if (!_isBusy && CurrentScreen == Screen.Room && _roomState != null) { _roomState.TryStartGame(_gameScenePath); }
    }

    // 현재 참여자 전원의 유효 데이터와 준비 상태 검사
    public bool AreAllPlayersReady()
    {
        if (_runner == null || !_runner.IsRunning) { return false; }
        int count = 0;
        foreach (PlayerRef id in _runner.ActivePlayers)
        {
            count++;
            if (!_players.TryGetValue(id, out PlayerRoomState player) || player == null ||
                !player.Object.IsValid || player.Player != id || player.Object.StateAuthority != id || !player.IsReady)
            {
                return false;
            }
        }
        return count >= 2 && count <= MaxPlayers;
    }

    // 스폰된 플레이어 데이터를 PlayerRef 키로 등록
    public void RegisterPlayer(PlayerRoomState player)
    {
        if (player == null || player.Runner != _runner) { return; }
        _players[player.Player] = player;
        NotifyChanged();
    }

    // 디스폰된 인스턴스와 일치하는 플레이어만 제거
    public void UnregisterPlayer(PlayerRef id, PlayerRoomState player)
    {
        if (_players.TryGetValue(id, out PlayerRoomState current) && current == player)
        {
            _players.Remove(id);
            NotifyChanged();
        }
    }

    // Scene 방 상태 객체 연결 및 방장 표시 갱신
    public void RegisterRoom(RoomGameState state)
    {
        if (state != null && state.Runner == _runner)
        {
            _roomState = state;
            NotifyRoomChanged();
        }
    }

    // 네트워크 시작 상태 수신에 따른 입력 잠금
    public void NotifyRoomChanged()
    {
        if (_roomState != null && _roomState.Object.IsValid && _roomState.IsStarting)
        {
            CurrentScreen = Screen.Loading;
            Status = "게임을 시작하는 중...";
        }
        NotifyChanged();
    }

    // 이벤트 기반 UI 상태 변경 통지
    public void NotifyChanged()
    {
        if (!_isDestroyed) { Changed?.Invoke(); }
    }

    // 표시용 방 이름의 공백과 길이 및 제어문자 검증
    public static bool TryValidateTitle(string input, out string title, out string error)
    {
        title = (input ?? "").Trim();
        error = "";
        // Trim으로 삭제되는 개행도 원본 입력 단계에서 거절
        foreach (char character in input ?? "")
        {
            if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
            {
                error = "개행과 제어문자는 사용할 수 없습니다.";
                return false;
            }
        }
        if (title.Length < 2 || title.Length > 20)
        {
            error = "방 이름은 앞뒤 공백을 제외하고 2~20글자로 입력해 주세요.";
            return false;
        }
        return true;
    }

    // Photon 커스텀 속성에서 표시 이름 추출
    public static string GetTitle(SessionInfo session)
    {
        return session != null && session.Properties != null &&
            session.Properties.TryGetValue("title", out SessionProperty value) && value.IsString
            ? (string)value : "이름 없는 방";
    }

    // 공개 상태와 정원 및 게임 종류 필터 검사
    public static bool IsJoinable(SessionInfo session)
    {
        return session != null && session.IsOpen && session.IsVisible && session.MaxPlayers == MaxPlayers &&
            session.PlayerCount >= 0 && session.PlayerCount < MaxPlayers && session.Properties != null &&
            session.Properties.TryGetValue("kind", out SessionProperty kind) && kind.IsString && (string)kind == RoomKind;
    }

    // 중복 연결 작업의 동기 진입 차단
    private bool BeginOperation(Screen expected)
    {
        if (_isDestroyed || !enabled || _isBusy || IsSceneLoading || CurrentScreen != expected) { return false; }
        _isBusy = true;
        NotifyChanged();
        return true;
    }

    // 비동기 작업 완료 후 UI 입력 복원
    private void EndOperation()
    {
        _isBusy = false;
        NotifyChanged();
    }

    // 별도 GameObject의 새 Runner 생성 및 Shared 로비 접속
    private async Task JoinLobbyAsync()
    {
        if (_isDestroyed) { return; }
        ClearRoom();
        ClearSessions();
        _isJoiningLobby = true;
        Status = CurrentScreen == Screen.Start ? "서버에 연결 중..." : "방 목록을 불러오는 중...";
        NotifyChanged();
        var settings = PhotonAppSettings.Global.AppSettings.GetCopy();
        if (string.IsNullOrWhiteSpace(settings.AppIdFusion)) { throw new InvalidOperationException("Photon Fusion App ID 미설정"); }
        settings.FixedRegion = "kr";
        var runnerObject = new GameObject("Shared NetworkRunner");
        DontDestroyOnLoad(runnerObject);
        _runner = runnerObject.AddComponent<NetworkRunner>();
        runnerObject.AddComponent<NetworkSceneManagerDefault>();
        _runner.AddCallbacks(this);
        NetworkRunner joining = _runner;
        StartGameResult result = await joining.JoinSessionLobby(SessionLobby.Shared, customAppSettings: settings);
        _isJoiningLobby = false;
        if (_isDestroyed) { return; }
        if (!result.Ok || _runner != joining)
        {
            Debug.LogError($"Photon 로비 접속 실패: {result.ShutdownReason}", this);
            await StopRunnerAsync();
            Status = "서버 연결에 실패했습니다. 다시 시도해 주세요.";
            return;
        }
        CurrentScreen = Screen.Lobby;
        Status = _hasSessionList ? "KR 로비에 연결되었습니다." : "방 목록을 불러오는 중...";
        SessionListChanged?.Invoke();
    }

    // 로비에서 사용한 Runner로 방 생성 또는 참가 및 실패 복구
    private async Task EnterRoomAsync(string sessionName, string title, bool create)
    {
        if (!BeginOperation(Screen.Lobby)) { return; }
        RoomError = "";
        _isJoiningLobby = false;
        Status = create ? "방을 만드는 중..." : "방에 참가하는 중...";
        NotifyChanged();
        try
        {
            if (_runner == null || _runner.IsShutdown) { throw new InvalidOperationException("로비 Runner 종료 상태"); }
            int menuIndex = SceneUtility.GetBuildIndexByScenePath(_menuScenePath);
            if (menuIndex < 0) { throw new InvalidOperationException("Build Profile의 MainMenu Scene 누락"); }
            var sceneInfo = new NetworkSceneInfo();
            sceneInfo.AddSceneRef(SceneRef.FromIndex(menuIndex), LoadSceneMode.Single);
            var args = new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = MaxPlayers,
                EnableClientSessionCreation = create,
                Scene = sceneInfo,
                SceneManager = _runner.GetComponent<NetworkSceneManagerDefault>()
            };
            if (create)
            {
                args.IsOpen = true;
                args.IsVisible = true;
                args.SessionProperties = new Dictionary<string, SessionProperty> { { "title", title }, { "kind", RoomKind } };
            }
            NetworkRunner joining = _runner;
            StartGameResult result = await joining.StartGame(args);
            if (_isDestroyed) { return; }
            if (!result.Ok || joining == null || joining != _runner || joining.SessionInfo.Name != sessionName)
            {
                throw new InvalidOperationException($"Photon 방 요청 실패: {result.ShutdownReason}");
            }
            ClearSessions();
            CurrentScreen = Screen.Room;
            Status = "준비 후 방장이 게임을 시작할 수 있습니다.";
            NotifyRoomChanged();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            if (_isDestroyed) { return; }
            RoomError = create ? "방을 만들 수 없습니다. 다시 시도해 주세요." : "방에 참가할 수 없습니다.";
            await StopRunnerAsync();
            CurrentScreen = Screen.Lobby;
            try { await JoinLobbyAsync(); }
            catch (Exception reconnectError) { await HandleConnectionErrorAsync(reconnectError); }
            Status = RoomError;
        }
        finally { EndOperation(); }
    }

    // 이전 Runner 참조와 콜백을 먼저 정리한 뒤 종료 대기
    private async Task StopRunnerAsync()
    {
        NetworkRunner stopping = _runner;
        _runner = null;
        _isJoiningLobby = false;
        ClearRoom();
        ClearSessions();
        if (stopping != null)
        {
            stopping.RemoveCallbacks(this);
            await stopping.Shutdown();
        }
    }

    // 예외 원인 기록 후 현재 화면에서 재시도 제공
    private async Task HandleConnectionErrorAsync(Exception exception)
    {
        Debug.LogException(exception, this);
        await StopRunnerAsync();
        Status = "서버 연결에 실패했습니다. 다시 시도해 주세요.";
    }

    // 이전 방의 데이터와 권한 참조 제거
    private void ClearRoom()
    {
        _players.Clear();
        _roomState = null;
        IsSceneLoading = false;
    }

    // 이전 로비 스냅샷 제거 및 재사용 UI 항목 비활성화 요청
    private void ClearSessions()
    {
        _sessions.Clear();
        _hasSessionList = false;
        SessionListChanged?.Invoke();
    }

    // 인원별 11개 그룹을 통한 안정적인 선형 시간 목록 교체
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
    {
        if (runner != _runner || (!_isJoiningLobby && (CurrentScreen != Screen.Lobby || _isBusy))) { return; }
        foreach (List<SessionInfo> group in _groups) { group.Clear(); }
        foreach (SessionInfo session in sessionList)
        {
            if (IsJoinable(session)) { _groups[session.PlayerCount].Add(session); }
        }
        _sessions.Clear();
        for (int count = MaxPlayers; count >= 0; count--) { _sessions.AddRange(_groups[count]); }
        _hasSessionList = true;
        Status = "KR 로비에 연결되었습니다.";
        SessionListChanged?.Invoke();
        NotifyChanged();
    }

    // 로컬 플레이어 입장 시 자신의 데이터 객체 하나만 스폰
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        if (runner != _runner) { return; }
        if (player == runner.LocalPlayer && !runner.TryGetPlayerObject(player, out _))
        {
            PlayerRoomState state = runner.Spawn(_playerPrefab, onBeforeSpawned: (source, networkObject) =>
            {
                networkObject.GetComponent<PlayerRoomState>().Initialize(player);
            });
            runner.SetPlayerObject(player, state.Object);
        }
        NotifyChanged();
    }

    // 퇴장 플레이어의 사전 등록 데이터 제거
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (runner != _runner) { return; }
        _players.Remove(player);
        NotifyChanged();
    }

    // 실제 종료 사유 기록 및 예기치 않은 연결 종료 화면 복원
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        Debug.LogWarning($"Photon ShutdownReason: {shutdownReason}", this);
        if (runner != _runner) { return; }
        runner.RemoveCallbacks(this);
        _runner = null;
        ClearRoom();
        ClearSessions();
        if (!_isBusy)
        {
            CurrentScreen = Screen.Start;
            Status = $"연결이 종료되었습니다. 다시 연결해 주세요. ({shutdownReason})";
            RoomError = "";
            NotifyChanged();
        }
    }

    // 게임 Scene 전환 중 입력 잠금
    public void OnSceneLoadStart(NetworkRunner runner)
    {
        if (runner != _runner) { return; }
        IsSceneLoading = true;
        // 최초 MainMenu 등록과 실제 게임 시작 전환 구분
        if (_roomState != null && _roomState.Object.IsValid && _roomState.IsStarting)
        {
            CurrentScreen = Screen.Loading;
            Status = "게임을 시작하는 중...";
        }
        NotifyChanged();
    }

    // 게임 Scene 진입 완료 후 메뉴 숨김
    public void OnSceneLoadDone(NetworkRunner runner)
    {
        if (runner != _runner) { return; }
        IsSceneLoading = false;
        if (SceneManager.GetSceneByPath(_gameScenePath).isLoaded)
        {
            CurrentScreen = Screen.Game;
        }
        NotifyChanged();
    }

    // 연결 계층의 실패 원인 기록
    public void OnConnectFailed(NetworkRunner runner, NetAddress address, NetConnectFailedReason reason)
    {
        Debug.LogWarning($"Photon 연결 실패: {reason}", this);
    }
    // 전송 계층의 연결 해제 원인 기록
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
    {
        Debug.LogWarning($"Photon 연결 해제: {reason}", this);
    }
    // Fusion 필수 계약의 미사용 입력 콜백
    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    // Fusion 필수 계약의 미사용 입력 누락 콜백
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    // Fusion 필수 계약의 미사용 연결 완료 콜백
    public void OnConnectedToServer(NetworkRunner runner) { }
    // Shared Mode에서 미사용인 서버 연결 승인 콜백
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    // Fusion 필수 계약의 미사용 사용자 메시지 콜백
#pragma warning disable CS0618
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
#pragma warning restore CS0618
    // Fusion 필수 계약의 미사용 인증 응답 콜백
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    // Shared Mode에서 미사용인 Host 이전 콜백
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken token) { }
    // Fusion 필수 계약의 미사용 신뢰 전송 콜백
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
    // Fusion 필수 계약의 미사용 신뢰 전송 진행 콜백
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    // Fusion 필수 계약의 미사용 관심 영역 진입 콜백
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    // Fusion 필수 계약의 미사용 관심 영역 이탈 콜백
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
}
