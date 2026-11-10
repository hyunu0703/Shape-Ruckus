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
    // 방 정원과 목록 필터 및 새로고침 제한
    public const int MaxPlayers = 10;
    public const string RoomKind = "shape-5v5-v2";
    public const float RefreshInterval = 5f;

    // 화면 전환 단계
    public enum Screen
    {
        Start,
        Lobby,
        Room,
        Loading,
        Game
    }

    // 플레이어 생성과 네트워크 Scene 경로 설정
    [SerializeField] private PlayerRoomState playerPrefab;
    [SerializeField] private string menuScenePath = "Assets/Scenes/MainMenu.unity";
    [SerializeField] private string gameScenePath = "Assets/Scenes/GameTest.unity";

    // 플레이어 식별 검색과 로비 목록 분류
    private readonly Dictionary<PlayerRef, PlayerRoomState> players = new Dictionary<PlayerRef, PlayerRoomState>(MaxPlayers);
    private readonly List<SessionInfo> sessions = new List<SessionInfo>();
    private readonly HashSet<string> occupiedRoomNames = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<SessionInfo>[] groups = new List<SessionInfo>[MaxPlayers + 1];

    // 현재 네트워크 연결과 방 전체 상태 참조
    private NetworkRunner runner;
    private RoomGameState roomState;

    // 방 이동과 뒤로 가기에도 유지하는 앱 단위 닉네임 점유
    private NicknameReservation nicknameReservation;
    private bool isRoomAccepted;
    private bool isInitialMenuSceneReady;
    public string Nickname { get; private set; } = "";
    public string NicknameError { get; private set; } = "";
    public bool HasNickname => this.nicknameReservation != null && this.nicknameReservation.IsHeld;

    // 비동기 작업과 연결 수명 및 목록 수신 상태
    private bool isBusy;
    private bool isJoiningLobby;
    private bool hasSessionList;
    private bool isDestroyed;
    private float lastRefresh = float.NegativeInfinity;

    // 외부 연결 참조와 화면 상태 조회
    public static SharedSession Instance { get; private set; }
    public NetworkRunner Runner => this.runner;
    public Screen CurrentScreen { get; private set; }
    public string Status { get; private set; } = "온라인 플레이를 눌러 시작하세요.";
    public string RoomError { get; private set; } = "";

    // 작업 가능 여부와 새로고침 제한 시간 조회
    public bool IsBusy => this.isBusy;
    public bool IsSceneLoading { get; private set; }
    public bool HasSessionList => this.hasSessionList;
    public float RefreshRemaining => Mathf.Max(0f, RefreshInterval - (Time.realtimeSinceStartup - this.lastRefresh));

    // 로비 목록과 방 대기실 데이터 조회
    public IReadOnlyList<SessionInfo> Sessions => this.sessions;
    public IReadOnlyDictionary<PlayerRef, PlayerRoomState> Players => this.players;
    public RoomGameState RoomState => this.roomState;
    public string RoomTitle => this.runner != null ? GetTitle(this.runner.SessionInfo) : "";

    // 상태와 방 목록 변경 통지
    public event Action Changed;
    public event Action SessionListChanged;


    // 영속 관리 객체 초기화 및 중복 Scene 인스턴스 정리
    private void Awake()
    {
        // Scene 재진입으로 생성된 중복 관리 객체만 제거
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        // 연결 관리 객체를 Scene 전환에 유지하고 인원별 분류 버퍼 초기화
        Instance = this;
        DontDestroyOnLoad(gameObject);

        for (int i = 0; i <= MaxPlayers; i++)
        {
            this.groups[i] = new List<SessionInfo>();
        }

        // 필수 플레이어 Prefab 누락 시 불완전한 연결 시작 차단
        if (this.playerPrefab == null)
        {
            Debug.LogError("SharedSession의 PlayerRoomState Prefab 누락", this);
            enabled = false;
        }
    }


    // 파괴된 UI와 이전 Runner로 향하는 콜백 차단
    private void OnDestroy()
    {
        // 늦게 완료되는 비동기 작업의 UI 접근 차단과 Runner 콜백 해제
        this.isDestroyed = true;
        this.nicknameReservation?.Dispose();
        this.nicknameReservation = null;

        if (this.runner != null)
        {
            this.runner.RemoveCallbacks(this);
            NetworkRunner stopping = this.runner;
            this.runner = null;
            // 별도 영속 Runner도 종료하여 관리 객체 없는 연결 잔류 방지
            _ = ReleaseRunnerAsync(stopping);
        }

        // 현재 인스턴스인 경우에만 전역 참조 해제
        if (Instance == this)
        {
            Instance = null;
        }

        // 파괴된 UI를 참조하는 이벤트 구독 정리
        Changed = null;
        SessionListChanged = null;
    }


    // 온라인 버튼 입력에 따른 최초 로비 연결
    public async Task ConnectAsync()
    {
        // 닉네임 등록 전 기존 진입점의 로비 우회 차단
        if (!HasNickname)
        {
            NicknameError = "닉네임을 먼저 등록해 주세요.";
            NotifyChanged();
            return;
        }

        // 시작 화면에서만 연결 잠금을 획득하고 중복 클릭 차단
        if (!BeginOperation(Screen.Start))
        {
            return;
        }

        // 로비 접속 실패는 복구 처리로 전달하고 완료 여부와 관계없이 입력 잠금 해제
        try
        {
            await JoinLobbyAsync();
        }
        catch (Exception exception)
        {
            await HandleConnectionErrorAsync(exception);
        }
        finally
        {
            EndOperation();
        }
    }


    // 입력한 닉네임의 서버 선점 후 최초 로비 연결
    public async Task RegisterNicknameAsync(string input)
    {
        // 앱에서 이미 선택한 이름은 재접속 시에도 동일하게 유지
        if (HasNickname)
        {
            await ConnectAsync();
            return;
        }

        if (!OnlineNameRules.TryValidate(string.IsNullOrEmpty(Nickname) ? input : Nickname,
            true, out string nickname, out string error))
        {
            NicknameError = error;
            NotifyChanged();
            return;
        }

        if (!BeginOperation(Screen.Start))
        {
            return;
        }

        NicknameReservation reservation = null;
        try
        {
            // 기존 점유 연결 정리 후 서버 생성 결과로 중복 여부 결정
            NicknameError = "";
            Status = "닉네임을 확인하는 중...";
            NotifyChanged();
            this.nicknameReservation?.Dispose();
            reservation = new NicknameReservation();
            this.nicknameReservation = reservation;

            if (!await reservation.AcquireAsync(nickname))
            {
                NicknameError = "이미 사용 중인 닉네임입니다.";
                Status = NicknameError;
                reservation.Dispose();
                this.nicknameReservation = null;
                return;
            }

            if (this.isDestroyed || this.nicknameReservation != reservation)
            {
                reservation.Dispose();
                return;
            }

            // 확정 닉네임을 방 이동과 Scene 전환에 유지
            Nickname = nickname;
            reservation.Lost += OnNicknameLost;
            await JoinLobbyAsync();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);

            if (reservation == null || !reservation.IsHeld)
            {
                reservation?.Dispose();
                this.nicknameReservation = null;
                NicknameError = "닉네임을 확인할 수 없습니다. 다시 시도해 주세요.";
            }

            await StopRunnerAsync();
            Status = "서버 연결에 실패했습니다. 다시 시도해 주세요.";
        }
        finally
        {
            EndOperation();
        }
    }


    // 닉네임 점유 상실 시 게임 연결을 종료하여 중복 이름 플레이 차단
    private async void OnNicknameLost()
    {
        // 종료 중이거나 이미 정리된 점유 연결의 통지 무시
        if (this.isDestroyed || this.nicknameReservation == null)
        {
            return;
        }

        this.nicknameReservation.Dispose();
        this.nicknameReservation = null;
        this.isBusy = true;
        NotifyChanged();
        try
        {
            await StopRunnerAsync();
            CurrentScreen = Screen.Start;
            NicknameError = "닉네임 연결이 끊겼습니다. 다시 연결해 주세요.";
            Status = NicknameError;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }
        finally
        {
            EndOperation();
        }
    }


    // 새 Runner를 사용하는 로비 새로고침 및 실패 후 재시도
    public async Task RefreshAsync()
    {
        // 5초 제한과 로비 작업 잠금을 함께 검사
        if (RefreshRemaining > 0f || !BeginOperation(Screen.Lobby))
        {
            return;
        }

        // 요청 시점을 먼저 기록하고 이전 Runner 종료 후 새 로비 연결 시작
        this.lastRefresh = Time.realtimeSinceStartup;
        try
        {
            await StopRunnerAsync();
            await JoinLobbyAsync();
        }
        catch (Exception exception)
        {
            await HandleConnectionErrorAsync(exception);
        }
        finally
        {
            EndOperation();
        }
    }


    // 표시 이름 검증 후 고유 세션 생성
    public async Task CreateRoomAsync(string input)
    {
        // 로비 밖이거나 다른 요청이 진행 중인 경우 생성 차단
        if (this.isBusy || CurrentScreen != Screen.Lobby)
        {
            return;
        }

        // 원본 입력 검증 실패 시 Photon 요청 없이 오류만 UI에 전달
        if (!TryValidateTitle(input, out string title, out string error))
        {
            RoomError = error;
            NotifyChanged();
            return;
        }

        // 수신한 방은 정원과 참가 가능 여부에 관계없이 중복 안내
        string sessionName = OnlineNameRules.GetKey("sr-room-v2-", title);

        if (this.occupiedRoomNames.Contains(sessionName))
        {
            RoomError = "이미 사용 중인 방 이름입니다.";
            NotifyChanged();
            return;
        }

        // 목록에 없는 숨겨진 방과 동시 요청도 동일 Photon 세션으로 매핑
        await EnterRoomAsync(sessionName, title, true);
    }


    // 최신 목록에 남아 있는 참가 가능 방만 참가 요청
    public async Task JoinRoomAsync(string sessionName)
    {
        // 로비 상태와 작업 가능 여부 확인
        if (this.isBusy || CurrentScreen != Screen.Lobby)
        {
            return;
        }

        // 최신 스냅샷에서 내부 이름과 참가 조건이 일치하는 방만 선택
        foreach (SessionInfo session in this.sessions)
        {
            if (session.Name == sessionName && IsJoinable(session))
            {
                await EnterRoomAsync(sessionName, null, false);
                return;
            }
        }

        // 목록에서 사라진 방은 참가 요청 없이 실패 안내로 처리
        Status = "방에 참가할 수 없습니다.";
        NotifyChanged();
    }


    // 로비 연결 종료 후 시작 화면 복원
    public async Task BackAsync()
    {
        // 로비 작업 잠금 획득 후에만 뒤로 가기 진행
        if (!BeginOperation(Screen.Lobby))
        {
            return;
        }

        // 연결과 이전 목록을 정리한 뒤 시작 화면으로 복원하며 실패 시 복구 처리
        try
        {
            await StopRunnerAsync();
            CurrentScreen = Screen.Start;
            Status = "온라인 플레이를 눌러 시작하세요.";
            RoomError = "";
        }
        catch (Exception exception)
        {
            await HandleConnectionErrorAsync(exception);
        }
        finally
        {
            EndOperation();
        }
    }


    // 방 퇴장 후 새 Runner로 Shared 로비 재접속
    public async Task LeaveRoomAsync()
    {
        // 방 대기실에서 중복되지 않는 퇴장 요청만 허용
        if (!BeginOperation(Screen.Room))
        {
            return;
        }

        // 기존 방 Runner 종료 후 로비 화면에서 새 Runner 접속으로 전환
        try
        {
            await StopRunnerAsync();
            CurrentScreen = Screen.Lobby;
            await JoinLobbyAsync();
        }
        catch (Exception exception)
        {
            await HandleConnectionErrorAsync(exception);
        }
        finally
        {
            EndOperation();
        }
    }


    // 빌드 환경 종료 전 네트워크 연결 정리
    public async Task QuitAsync()
    {
#if !UNITY_EDITOR
        // 빌드 환경에서 진행 중인 작업과 종료 요청의 충돌 차단
        if (this.isBusy)
        {
            return;
        }

        // 종료 입력을 잠근 뒤 네트워크 정리를 기다리고 앱 종료 요청
        this.isBusy = true;
        NotifyChanged();
        try
        {
            await StopRunnerAsync();
            if (this.nicknameReservation != null)
            {
                await this.nicknameReservation.ReleaseAsync();
                this.nicknameReservation = null;
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }

        Application.Quit();
#else
        await Task.CompletedTask;
#endif
    }


    // 로컬 State Authority 플레이어의 준비 상태 전환
    public void ToggleReady()
    {
        // 작업 중이거나 Scene 전환 중인 경우 준비 입력 차단
        if (this.isBusy || IsSceneLoading || CurrentScreen != Screen.Room || this.runner == null)
        {
            return;
        }

        // 로컬 PlayerRef에 등록된 데이터 객체로만 준비 변경 전달
        if (this.players.TryGetValue(this.runner.LocalPlayer, out PlayerRoomState player) && player != null)
        {
            player.ToggleReady();
        }
    }


    // 네트워크 방 상태 객체로 시작 요청 전달
    public void StartMatch()
    {
        // 대기실 요청만 방 전체 상태 객체에 전달하여 권한과 준비 조건 재검사
        if (!this.isBusy && CurrentScreen == Screen.Room && this.roomState != null)
        {
            this.roomState.TryStartGame(this.gameScenePath);
        }
    }


    // 현재 참여자 전원의 유효 데이터와 준비 상태 검사
    public bool AreAllPlayersReady()
    {
        // 실행 중인 방 연결이 없는 경우 시작 조건 불충족 처리
        if (this.runner == null || !this.runner.IsRunning)
        {
            return false;
        }

        int count = 0;

        // 실제 활성 참가자를 기준으로 데이터 도착 여부와 소유자 및 준비 상태 검사
        foreach (PlayerRef id in this.runner.ActivePlayers)
        {
            count++;

            if (!this.players.TryGetValue(id, out PlayerRoomState player) ||
                player == null ||
                player.Object == null ||
                !player.Object.IsValid ||
                player.Player != id ||
                player.Object.StateAuthority != id ||
                (id != this.runner.GetMasterClient() && !player.IsReady))
            {
                return false;
            }
        }

        // 데이터 누락 없이 검사한 참가자 수에 최소 인원과 정원 조건 적용
        return count >= 2 && count <= MaxPlayers;
    }


    // 스폰된 플레이어 데이터를 PlayerRef 키로 등록
    public void RegisterPlayer(PlayerRoomState player)
    {
        // 이전 Runner에서 전달된 플레이어 데이터 등록 차단
        if (player == null || player.Runner != this.runner || player.Object == null ||
            !player.Object.IsValid || player.Player != player.Object.StateAuthority)
        {
            return;
        }

        // 현재 식별자에 데이터를 등록한 뒤 플레이어 목록 갱신 통지
        this.players[player.Player] = player;
        NotifyChanged();
    }


    // 디스폰된 인스턴스와 일치하는 플레이어만 제거
    public void UnregisterPlayer(PlayerRef id, PlayerRoomState player)
    {
        // 같은 식별자의 새 객체를 지우지 않도록 등록 인스턴스 일치 여부 검사
        if (this.players.TryGetValue(id, out PlayerRoomState current) && current == player)
        {
            this.players.Remove(id);
            NotifyChanged();
        }
    }


    // Scene 방 상태 객체 연결 및 방장 표시 갱신
    public void RegisterRoom(RoomGameState state)
    {
        // 현재 Runner의 방 상태만 연결하고 시작 상태 및 방장 표시 갱신
        if (state != null && state.Runner == this.runner)
        {
            this.roomState = state;
            NotifyRoomChanged();
        }
    }


    // 네트워크 시작 상태 수신에 따른 입력 잠금
    public void NotifyRoomChanged()
    {
        // 새 방장은 자신의 State Authority로 기존 네트워크 준비 값을 갱신
        if (this.runner != null && this.runner.IsRunning &&
            this.players.TryGetValue(this.runner.LocalPlayer, out PlayerRoomState player) && player != null)
        {
            player.EnsureHostReady();
        }

        // 늦은 시작 상태 통지가 완료된 게임 화면을 로딩 화면으로 되돌리는 경우 차단
        if (CurrentScreen != Screen.Game && this.roomState != null && this.roomState.Object != null &&
            this.roomState.Object.IsValid && this.roomState.IsStarting)
        {
            CurrentScreen = Screen.Loading;
            Status = "게임을 시작하는 중...";
        }

        // 시작 상태와 권한 변화가 버튼에 반영되도록 UI에 통지
        NotifyChanged();
    }


    // 이벤트 기반 UI 상태 변경 통지
    public void NotifyChanged()
    {
        // 관리 객체가 살아 있는 동안에만 구독 UI에 변경 전달
        if (!this.isDestroyed)
        {
            Changed?.Invoke();
        }
    }


    // 표시용 방 이름의 공백과 길이 및 제어문자 검증
    public static bool TryValidateTitle(string input, out string title, out string error)
    {
        // UI와 직접 호출의 최대 24글자 조건 통합
        return OnlineNameRules.TryValidate(input, false, out title, out error);
    }


    // Photon 커스텀 속성에서 표시 이름 추출
    public static string GetTitle(SessionInfo session)
    {
        // 문자열 title 속성만 표시 이름으로 사용하고 누락 시 기본 안내 반환
        return session != null &&
            session.Properties != null &&
            session.Properties.TryGetValue("title", out SessionProperty value) &&
            value.IsString ? (string)value : "이름 없는 방";
    }


    // 공개 상태와 정원 및 게임 종류 필터 검사
    public static bool IsJoinable(SessionInfo session)
    {
        // 공개 여부와 빈 자리 및 게임 종류가 모두 일치하는 방만 목록에 허용
        return session != null &&
            session.IsOpen &&
            session.IsVisible &&
            session.MaxPlayers == MaxPlayers &&
            session.PlayerCount >= 0 &&
            session.PlayerCount < MaxPlayers &&
            session.Properties != null &&
            session.Properties.TryGetValue("kind", out SessionProperty kind) &&
            kind.IsString &&
            (string)kind == RoomKind;
    }


    // 중복 연결 작업의 동기 진입 차단
    private bool BeginOperation(Screen expected)
    {
        // 객체 수명과 예상 화면 및 진행 중인 작업을 검사하여 재진입 차단
        if (this.isDestroyed || !enabled || this.isBusy || IsSceneLoading || CurrentScreen != expected)
        {
            return false;
        }

        // 비동기 요청 전에 잠금 상태를 UI에 먼저 전달
        this.isBusy = true;
        NotifyChanged();
        return true;
    }


    // 비동기 작업 완료 후 UI 입력 복원
    private void EndOperation()
    {
        // 작업 잠금을 해제한 상태로 버튼 사용 가능 여부 재평가 요청
        this.isBusy = false;
        NotifyChanged();
    }


    // 별도 GameObject의 새 Runner 생성 및 Shared 로비 접속
    private async Task JoinLobbyAsync()
    {
        // 이미 파괴된 관리 객체의 후속 연결 생성 차단
        if (this.isDestroyed || !HasNickname)
        {
            return;
        }

        // 이전 방 데이터와 목록을 비우고 연결 진행 메시지 전달
        ClearRoom();
        ClearSessions();
        this.isJoiningLobby = true;
        Status = CurrentScreen == Screen.Start ? "서버에 연결 중..." : "방 목록을 불러오는 중...";
        NotifyChanged();

        // 기존 App ID를 가진 설정 복사본을 확보하고 필수 설정 검사
        var settings = PhotonAppSettings.Global.AppSettings.GetCopy();

        if (string.IsNullOrWhiteSpace(settings.AppIdFusion))
        {
            throw new InvalidOperationException("Photon Fusion App ID 미설정");
        }

        // KR 리전의 별도 영속 Runner를 생성하고 Scene 관리자와 콜백 연결
        settings.FixedRegion = "kr";
        var runnerObject = new GameObject("Shared NetworkRunner");
        DontDestroyOnLoad(runnerObject);
        this.runner = runnerObject.AddComponent<NetworkRunner>();
        runnerObject.AddComponent<NetworkSceneManagerDefault>();
        this.runner.AddCallbacks(this);
        NetworkRunner joining = this.runner;

        // 생성한 Runner의 Shared 로비 접속 결과 대기
        StartGameResult result = await joining.JoinSessionLobby(SessionLobby.Shared, customAppSettings: settings);
        this.isJoiningLobby = false;

        if (this.isDestroyed)
        {
            return;
        }

        // 실패하거나 현재 Runner가 바뀐 경우 연결 참조 정리와 재시도 안내
        if (!result.Ok || this.runner != joining)
        {
            Debug.LogError($"Photon 로비 접속 실패: {result.ShutdownReason}", this);
            await StopRunnerAsync();
            Status = "서버 연결에 실패했습니다. 다시 시도해 주세요.";
            return;
        }

        // 유효한 접속 완료 후에만 로비 화면과 수신 목록 갱신 통지
        CurrentScreen = Screen.Lobby;
        Status = this.hasSessionList ? "KR 로비에 연결되었습니다." : "방 목록을 불러오는 중...";
        SessionListChanged?.Invoke();
    }


    // 로비에서 사용한 Runner로 방 생성 또는 참가 및 실패 복구
    private async Task EnterRoomAsync(string sessionName, string title, bool create)
    {
        // 방 요청 전에 로비 작업 잠금을 획득하여 목록 클릭 재진입 차단
        if (!BeginOperation(Screen.Lobby))
        {
            return;
        }

        // 이전 오류를 지우고 오래된 목록 콜백의 화면 복원 차단
        RoomError = "";
        this.isJoiningLobby = false;
        Status = create ? "방을 만드는 중..." : "방에 참가하는 중...";
        NotifyChanged();
        string creationId = create ? Guid.NewGuid().ToString("N") : null;
        bool duplicateTitle = false;
        try
        {
            // 종료된 Runner로의 요청을 막고 초기 네트워크 Scene 등록 검사
            if (!HasNickname || this.runner == null || this.runner.IsShutdown)
            {
                throw new InvalidOperationException("로비 Runner 종료 상태");
            }

            int menuIndex = SceneUtility.GetBuildIndexByScenePath(this.menuScenePath);

            if (menuIndex < 0)
            {
                throw new InvalidOperationException("Build Profile의 MainMenu Scene 누락");
            }

            // MainMenu를 네트워크 Scene으로 등록하고 기존 Runner의 Scene 관리자 재사용
            var sceneInfo = new NetworkSceneInfo();
            sceneInfo.AddSceneRef(SceneRef.FromIndex(menuIndex), LoadSceneMode.Single);
            var args = new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = MaxPlayers,
                EnableClientSessionCreation = create,
                Scene = sceneInfo,
                SceneManager = this.runner.GetComponent<NetworkSceneManagerDefault>()
            };

            // 생성 요청에만 공개 속성과 표시 이름을 설정하고 참가 요청의 임의 생성 차단
            if (create)
            {
                args.IsOpen = false;
                args.IsVisible = true;
                args.SessionProperties = new Dictionary<string, SessionProperty>
                {
                    {
                        "title",
                        title
                    },
                    {
                        "kind",
                        RoomKind
                    },
                    {
                        "creationId",
                        creationId
                    }
                };
            }

            NetworkRunner joining = this.runner;

            // 로비에서 사용한 Runner로 생성 또는 참가 요청 후 실제 세션 일치 여부 검사
            StartGameResult result = await joining.StartGame(args);

            if (this.isDestroyed)
            {
                return;
            }

            if (!result.Ok || joining == null || joining != this.runner || joining.SessionInfo.Name != sessionName)
            {
                duplicateTitle = create && (result.ShutdownReason == ShutdownReason.GameClosed ||
                    result.ShutdownReason == ShutdownReason.GameIsFull);
                throw new InvalidOperationException($"Photon 방 요청 실패: {result.ShutdownReason}");
            }

            // Shared의 JoinOrCreate 결과가 기존 방인 경우 생성 성공으로 처리하지 않고 즉시 퇴장
            if (create && (!joining.SessionInfo.Properties.TryGetValue("creationId", out SessionProperty actualId) ||
                !actualId.IsString || (string)actualId != creationId))
            {
                duplicateTitle = true;
                throw new InvalidOperationException("이미 사용 중인 방 이름");
            }

            if (!HasNickname)
            {
                throw new InvalidOperationException("닉네임 점유 연결 종료");
            }

            // 생성자 확인 이전에는 플레이어를 스폰하지 않고 성공한 신규 방만 개방
            this.isRoomAccepted = true;
            SpawnLocalPlayer(joining);

            if (create)
            {
                joining.SessionInfo.IsOpen = true;
            }

            // 성공한 방의 대기실로 전환하고 로비 스냅샷 정리
            ClearSessions();

            // Scene 완료 콜백이 먼저 도착한 경우 게임 화면 상태 보존
            if (CurrentScreen != Screen.Game)
            {
                CurrentScreen = Screen.Room;
            }

            Status = "준비 후 방장이 게임을 시작할 수 있습니다.";
            NotifyRoomChanged();
        }
        catch (Exception exception)
        {
            if (this.isDestroyed)
            {
                return;
            }

            // 사용자 입력 중복은 일반 오류로 안내하고 예기치 않은 실패만 예외 기록
            if (!duplicateTitle)
            {
                Debug.LogException(exception, this);
            }

            // 실패한 방 연결을 폐기하고 오류와 입력값을 유지한 채 새 로비 연결 시도
            RoomError = duplicateTitle ? "이미 사용 중인 방 이름입니다." :
                (create ? "방을 만들 수 없습니다. 이미 사용 중인 이름인지 확인해 주세요." : "방에 참가할 수 없습니다.");
            await StopRunnerAsync();
            CurrentScreen = Screen.Lobby;
            try
            {
                await JoinLobbyAsync();
            }
            catch (Exception reconnectError)
            {
                await HandleConnectionErrorAsync(reconnectError);
            }

            Status = RoomError;
        }

        // 성공과 복구 실패 모두에서 작업 잠금 해제
        finally
        {
            EndOperation();
        }
    }


    // 이전 Runner 참조와 콜백을 먼저 정리한 뒤 종료 대기
    private async Task StopRunnerAsync()
    {
        // 이전 Runner를 따로 보관하고 현재 참조와 방 데이터를 먼저 비워 늦은 콜백 차단
        NetworkRunner stopping = this.runner;
        this.runner = null;
        this.isJoiningLobby = false;
        ClearRoom();
        ClearSessions();

        // 종료 콜백을 해제한 이전 Runner의 연결 종료 대기
        if (stopping != null)
        {
            stopping.RemoveCallbacks(this);
            await ReleaseRunnerAsync(stopping);
        }
    }


    // 관리 객체 수명과 독립적으로 Runner 종료 및 실패 시 잔류 오브젝트 정리
    private static async Task ReleaseRunnerAsync(NetworkRunner stopping)
    {
        // 이미 파괴된 Runner에는 네트워크 API 호출 생략
        if (stopping == null)
        {
            return;
        }

        try
        {
            await stopping.Shutdown();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            // Shutdown 중 예외가 발생해도 별도 영속 GameObject 정리
            if (stopping != null)
            {
                Destroy(stopping.gameObject);
            }
        }
    }


    // 예외 원인 기록 후 현재 화면에서 재시도 제공
    private async Task HandleConnectionErrorAsync(Exception exception)
    {
        // 실제 예외를 기록하고 연결을 정리한 뒤 사용자에게 재시도 안내 전달
        Debug.LogException(exception, this);
        await StopRunnerAsync();
        Status = "서버 연결에 실패했습니다. 다시 시도해 주세요.";
    }


    // 이전 방의 데이터와 권한 참조 제거
    private void ClearRoom()
    {
        // 이전 참가자와 방 권한 참조 및 Scene 로딩 상태를 함께 초기화
        this.players.Clear();
        this.isRoomAccepted = false;
        this.isInitialMenuSceneReady = false;
        this.roomState = null;
        IsSceneLoading = false;
    }


    // 이전 로비 스냅샷 제거 및 재사용 UI 항목 비활성화 요청
    private void ClearSessions()
    {
        // 이전 목록 스냅샷을 비우고 재사용 UI 항목 숨김 요청
        this.sessions.Clear();
        this.occupiedRoomNames.Clear();
        this.hasSessionList = false;

        // 비표시 분류 버퍼에서도 이전 SessionInfo 참조 해제
        foreach (List<SessionInfo> group in this.groups)
        {
            if (group != null)
            {
                group.Clear();
            }
        }

        SessionListChanged?.Invoke();
    }


    // 인원별 11개 그룹을 통한 안정적인 선형 시간 목록 교체
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
    {
        // 이전 Runner 또는 방 진입 중 수신된 오래된 목록 콜백 무시
        if (runner != this.runner || (!this.isJoiningLobby && (CurrentScreen != Screen.Lobby || this.isBusy)))
        {
            return;
        }

        // 이전 분류 결과를 비우고 기존 버퍼 재사용
        foreach (List<SessionInfo> group in this.groups)
        {
            group.Clear();
        }

        // 참가 가능 방만 인원수에 대응하는 그룹에 수신 순서대로 추가
        this.occupiedRoomNames.Clear();
        foreach (SessionInfo session in sessionList)
        {
            if (session != null)
            {
                this.occupiedRoomNames.Add(session.Name);
            }

            if (IsJoinable(session))
            {
                this.groups[session.PlayerCount].Add(session);
            }
        }

        this.sessions.Clear();

        // 높은 인원 그룹부터 연결하여 동률 순서를 유지하는 선형 시간 목록 구성
        for (int count = MaxPlayers; count >= 0; count--)
        {
            this.sessions.AddRange(this.groups[count]);
        }

        // 수신 완료 상태와 새 전체 목록을 UI에 전달
        this.hasSessionList = true;
        Status = "KR 로비에 연결되었습니다.";
        SessionListChanged?.Invoke();
        NotifyChanged();
    }


    // 로컬 플레이어 입장 시 자신의 데이터 객체 하나만 스폰
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        // 현재 연결에 속하지 않는 입장 콜백 무시
        if (runner != this.runner)
        {
            return;
        }

        // 자신의 입장에서만 초기 데이터를 넣어 객체 하나를 스폰하고 PlayerRef와 연결
        if (player == runner.LocalPlayer && this.isRoomAccepted)
        {
            SpawnLocalPlayer(runner);
        }

        // 입장 결과에 맞춰 참가자 수와 시작 조건 재평가 요청
        NotifyChanged();
    }


    // 닉네임 점유와 방 생성 결과를 확인한 로컬 플레이어만 스폰
    private void SpawnLocalPlayer(NetworkRunner runner)
    {
        // 초기 씬 등록 완료 전 스폰과 이전 연결 및 중복 스폰 차단
        if (runner != this.runner || !HasNickname || !this.isRoomAccepted ||
            !this.isInitialMenuSceneReady || IsSceneLoading ||
            runner.TryGetPlayerObject(runner.LocalPlayer, out _))
        {
            return;
        }

        PlayerRef player = runner.LocalPlayer;
        PlayerRoomState state = runner.Spawn(this.playerPrefab, onBeforeSpawned: (source, networkObject) =>
        {
            networkObject.GetComponent<PlayerRoomState>().Initialize(player);
        });
        runner.SetPlayerObject(player, state.Object);
    }


    // 퇴장 플레이어의 사전 등록 데이터 제거
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        // 이전 연결의 퇴장 알림이 현재 방 목록을 변경하지 않도록 차단
        if (runner != this.runner)
        {
            return;
        }

        // 퇴장 식별자의 로컬 등록을 제거하고 대기실 갱신 통지
        this.players.Remove(player);
        NotifyChanged();
    }


    // 실제 종료 사유 기록 및 예기치 않은 연결 종료 화면 복원
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        // Photon에서 받은 실제 종료 사유를 먼저 기록
        Debug.LogWarning($"Photon ShutdownReason: {shutdownReason}", this);

        // 이미 정리된 이전 Runner의 종료 알림은 현재 상태에 미반영
        if (runner != this.runner)
        {
            return;
        }

        // 종료된 연결과 플레이어 및 목록 참조를 함께 정리
        runner.RemoveCallbacks(this);
        this.runner = null;
        ClearRoom();
        ClearSessions();

        // 별도 복구 작업이 없는 예상치 못한 종료만 시작 화면의 재연결 안내로 전환
        if (!this.isBusy)
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
        // 현재 Runner의 Scene 작업만 로딩 상태에 반영
        if (runner != this.runner)
        {
            return;
        }

        // Scene 등록과 전환이 끝날 때까지 중복 입력 잠금
        IsSceneLoading = true;

        // 최초 MainMenu 등록과 실제 게임 시작 전환 구분
        if (this.roomState != null && this.roomState.Object.IsValid && this.roomState.IsStarting)
        {
            CurrentScreen = Screen.Loading;
            Status = "게임을 시작하는 중...";
        }

        NotifyChanged();
    }


    // 게임 Scene 진입 완료 후 메뉴 숨김
    public void OnSceneLoadDone(NetworkRunner runner)
    {
        // 이전 연결의 Scene 완료 알림 무시
        if (runner != this.runner)
        {
            return;
        }

        // 로딩 잠금을 해제하고 실제 게임 Scene이 있으면 메뉴 숨김 상태로 전환
        IsSceneLoading = false;

        if (SceneManager.GetSceneByPath(this.gameScenePath).isLoaded)
        {
            CurrentScreen = Screen.Game;
        }
        else if (SceneManager.GetSceneByPath(this.menuScenePath).isLoaded)
        {
            // 초기 씬 정리가 끝난 뒤에만 스폰한다. 입장 승인과 씬 완료의 도착 순서는 무관하다.
            this.isInitialMenuSceneReady = true;
            if (this.isRoomAccepted)
            {
                SpawnLocalPlayer(runner);
            }
        }

        // 최종 화면과 버튼 상태를 UI에 전달
        NotifyChanged();
    }


    // 연결 계층의 실패 원인 기록
    public void OnConnectFailed(NetworkRunner runner, NetAddress address, NetConnectFailedReason reason)
    {
        // 연결 계층에서 전달된 실패 사유를 문제 추적용으로 기록
        Debug.LogWarning($"Photon 연결 실패: {reason}", this);
    }


    // 전송 계층의 연결 해제 원인 기록
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
    {
        // 전송 계층에서 전달된 해제 사유를 종료 로그와 함께 추적
        Debug.LogWarning($"Photon 연결 해제: {reason}", this);
    }


    // Fusion 필수 계약의 미사용 입력 콜백
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
    }


    // Fusion 필수 계약의 미사용 입력 누락 콜백
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
    {
    }


    // Fusion 필수 계약의 미사용 연결 완료 콜백
    public void OnConnectedToServer(NetworkRunner runner)
    {
    }


    // Shared Mode에서 미사용인 서버 연결 승인 콜백
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
    {
    }


    // Fusion 필수 계약의 미사용 사용자 메시지 콜백
#pragma warning disable CS0618
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
    {
    }

#pragma warning restore CS0618


    // Fusion 필수 계약의 미사용 인증 응답 콜백
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
    {
    }


    // Shared Mode에서 미사용인 Host 이전 콜백
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken token)
    {
    }


    // Fusion 필수 계약의 미사용 신뢰 전송 콜백
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data)
    {
    }


    // Fusion 필수 계약의 미사용 신뢰 전송 진행 콜백
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress)
    {
    }


    // Fusion 필수 계약의 미사용 관심 영역 진입 콜백
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
    {
    }


    // Fusion 필수 계약의 미사용 관심 영역 이탈 콜백
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
    {
    }
}
