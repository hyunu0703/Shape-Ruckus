using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 시작 화면과 로비 및 방 대기실의 이벤트 기반 uGUI 표시와 버튼 입력 전달
/// </summary>
public sealed class LobbyUI : MonoBehaviour
{
    // 네트워크 상태와 공통 화면 표시
    [SerializeField] private SharedSession session;
    [SerializeField] private Canvas canvas;
    [SerializeField] private TMP_Text status;

    // 시작 화면과 접속 및 종료 입력
    [SerializeField] private GameObject startPanel;
    [SerializeField] private Button online;
    [SerializeField] private Button quit;

    // 최초 온라인 진입의 닉네임 입력과 중복 오류 표시
    [SerializeField] private GameObject nicknamePopup;
    [SerializeField] private TMP_InputField nickname;
    [SerializeField] private TMP_Text nicknameError;
    [SerializeField] private Button confirmNickname;
    [SerializeField] private Button cancelNickname;

    // 로비 화면과 탐색 입력
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private CanvasGroup lobbyControls;
    [SerializeField] private Button back;
    [SerializeField] private Button refresh;
    [SerializeField] private Button openCreate;
    private Coroutine refreshCooldown;

    // 로비 방 목록 표시와 항목 재사용
    [SerializeField] private TMP_Text emptyList;
    [SerializeField] private RectTransform listContent;
    [SerializeField] private RoomListItem roomItemPrefab;
    private readonly List<RoomListItem> items = new List<RoomListItem>();

    // 방 생성 팝업과 이름 입력 및 오류 표시
    [SerializeField] private GameObject createPopup;
    [SerializeField] private TMP_InputField roomName;
    [SerializeField] private TMP_Text createError;
    [SerializeField] private Button create;
    [SerializeField] private Button cancelCreate;

    // 방 대기실 정보와 플레이어 목록 표시
    [SerializeField] private GameObject roomPanel;
    [SerializeField] private TMP_Text roomTitle;
    [SerializeField] private TMP_Text playerCount;
    [SerializeField] private TMP_Text[] playerSlots;
    private Image[] playerSlotBackgrounds;
    private Color[] playerSlotDefaultColors;
    private static readonly Color ReadySlotColor = new Color32(32, 59, 92, 255);
    private static readonly Color NotReadySlotColor = new Color32(92, 44, 50, 255);

    // 준비 상태와 게임 시작 및 퇴장 입력
    [SerializeField] private TMP_Text readyLabel;
    [SerializeField] private Button ready;
    [SerializeField] private Button startGame;
    [SerializeField] private Button leave;

    // 필수 참조 검증과 이벤트 구독 상태 및 반복 라벨 접근 캐시
    private bool isConfigured;
    private bool isSubscribed;
    private TMP_Text onlineLabel;
    private TMP_Text refreshLabel;
    private TMP_Text lobbyTitle;


    // 필수 UI 참조 검증
    private void Awake()
    {
        // 화면 구성에 필요한 참조와 10개 슬롯 확인 후 누락 시 UI 동작 중단
        UnityEngine.Object[] required =
        {
            this.session, this.canvas, this.status, this.startPanel, this.online, this.quit,
            this.lobbyPanel, this.lobbyControls, this.back, this.refresh, this.openCreate,
            this.emptyList, this.listContent, this.roomItemPrefab, this.createPopup,
            this.roomName, this.createError, this.create, this.cancelCreate, this.roomPanel,
            this.roomTitle, this.playerCount, this.readyLabel, this.ready, this.startGame, this.leave,
            this.nicknamePopup, this.nickname, this.nicknameError, this.confirmNickname, this.cancelNickname
        };

        foreach (UnityEngine.Object reference in required)
        {
            if (reference == null)
            {
                Debug.LogError("LobbyUI의 필수 Scene 참조 누락", this);
                enabled = false;
                return;
            }
        }

        if (this.playerSlots == null || this.playerSlots.Length != SharedSession.MaxPlayers)
        {
            Debug.LogError("LobbyUI의 플레이어 슬롯 10개 설정 필요", this);
            enabled = false;
            return;
        }

        foreach (TMP_Text slot in this.playerSlots)
        {
            if (slot == null)
            {
                Debug.LogError("LobbyUI의 플레이어 슬롯 텍스트 누락", this);
                enabled = false;
                return;
            }
        }

        // 기존 슬롯의 부모 패널과 색상을 한 번만 보관하여 방장과 빈 슬롯에 재사용
        this.playerSlotBackgrounds = new Image[this.playerSlots.Length];
        this.playerSlotDefaultColors = new Color[this.playerSlots.Length];
        for (int i = 0; i < this.playerSlots.Length; i++)
        {
            Transform panel = this.playerSlots[i].transform.parent;
            Image background = panel != null ? panel.GetComponent<Image>() : null;
            if (background == null)
            {
                Debug.LogError("LobbyUI의 플레이어 슬롯 배경 이미지 누락", this);
                enabled = false;
                return;
            }

            this.playerSlotBackgrounds[i] = background;
            this.playerSlotDefaultColors[i] = background.color;
        }

        // 초기 비활성 Panel의 라벨도 포함하여 한 번만 검색
        this.onlineLabel = this.online.GetComponentInChildren<TMP_Text>(true);
        this.refreshLabel = this.refresh.GetComponentInChildren<TMP_Text>(true);
        Transform titleTransform = this.lobbyPanel.transform.Find("LobbyTitle");
        this.lobbyTitle = titleTransform != null ? titleTransform.GetComponent<TMP_Text>() : null;

        if (this.onlineLabel == null || this.refreshLabel == null || this.lobbyTitle == null)
        {
            Debug.LogError("LobbyUI의 버튼 라벨 또는 로비 제목 누락", this);
            enabled = false;
            return;
        }

        // Inspector 설정과 관계없이 실제 입력 단계에도 최대 길이 적용
        this.nickname.characterLimit = OnlineNameRules.MaxNicknameLength;
        this.roomName.characterLimit = OnlineNameRules.MaxRoomTitleLength;
        WebGLNameInput.Attach(this.nickname);
        WebGLNameInput.Attach(this.roomName);
        this.roomTitle.richText = false;
        this.lobbyTitle.richText = false;

        foreach (TMP_Text slot in this.playerSlots)
        {
            slot.richText = false;
        }

        this.isConfigured = true;
    }


    // 화면 활성 기간의 네트워크 및 버튼 이벤트 구독
    private void OnEnable()
    {
        // 필수 참조 검증 실패와 중복 구독 및 세션 참조 소멸 시 연결 생략
        if (!this.isConfigured || this.isSubscribed || this.session == null)
        {
            return;
        }

        // 네트워크 변경을 화면 갱신에 연결하고 버튼 입력을 세션 작업에 연결
        this.isSubscribed = true;
        this.session.Changed += Render;
        this.session.SessionListChanged += RenderSessions;
        this.online.onClick.AddListener(OnOnline);
        this.quit.onClick.AddListener(OnQuit);
        this.confirmNickname.onClick.AddListener(OnConfirmNickname);
        this.cancelNickname.onClick.AddListener(CloseNickname);
        this.nickname.onSubmit.AddListener(OnSubmitNickname);
        this.nickname.onValidateInput += ValidateNicknameCharacter;
        this.back.onClick.AddListener(OnBack);
        this.refresh.onClick.AddListener(OnRefresh);
        this.openCreate.onClick.AddListener(OpenCreate);
        this.create.onClick.AddListener(OnCreate);
        this.cancelCreate.onClick.AddListener(CloseCreate);
        this.ready.onClick.AddListener(this.session.ToggleReady);
        this.startGame.onClick.AddListener(this.session.StartMatch);
        this.leave.onClick.AddListener(OnLeave);

        // 현재 데이터로 화면 초기화 후 재활성화 이전의 새로고침 대기 복원
        RenderSessions();
        Render();
        EnsureRefreshCooldown();
    }


    // 비활성화 시 구독과 단일 새로고침 대기 해제
    private void OnDisable()
    {
        // Awake 검증 실패로 구독 전 비활성화된 경우 부분 참조 접근 차단
        if (!this.isSubscribed)
        {
            return;
        }

        this.isSubscribed = false;

        // 세션이 남아 있는 경우 네트워크 이벤트와 세션 버튼 연결 해제
        if (this.session != null)
        {
            this.session.Changed -= Render;
            this.session.SessionListChanged -= RenderSessions;
            this.ready.onClick.RemoveListener(this.session.ToggleReady);
            this.startGame.onClick.RemoveListener(this.session.StartMatch);
        }

        // 재활성화 시 클릭이 중복 처리되지 않도록 나머지 버튼 구독 해제
        this.online.onClick.RemoveListener(OnOnline);
        this.quit.onClick.RemoveListener(OnQuit);
        this.confirmNickname.onClick.RemoveListener(OnConfirmNickname);
        this.cancelNickname.onClick.RemoveListener(CloseNickname);
        this.nickname.onSubmit.RemoveListener(OnSubmitNickname);
        this.nickname.onValidateInput -= ValidateNicknameCharacter;
        this.back.onClick.RemoveListener(OnBack);
        this.refresh.onClick.RemoveListener(OnRefresh);
        this.openCreate.onClick.RemoveListener(OpenCreate);
        this.create.onClick.RemoveListener(OnCreate);
        this.cancelCreate.onClick.RemoveListener(CloseCreate);
        this.leave.onClick.RemoveListener(OnLeave);

        // 중단된 대기의 실행 핸들까지 정리하여 다음 예약 허용
        if (this.refreshCooldown != null)
        {
            StopCoroutine(this.refreshCooldown);
            this.refreshCooldown = null;
        }
    }


    // 네트워크 상태에 따른 Panel과 입력 가능 상태 반영
    private void Render()
    {
        // 세션 참조가 없는 경우 화면 갱신 중단
        if (this.session == null)
        {
            return;
        }

        // 세션의 화면 단계와 작업 상태를 기준으로 표시 화면과 입력 잠금 결정
        SharedSession.Screen screen = this.session.CurrentScreen;
        bool idle = !this.session.IsBusy && !this.session.IsSceneLoading;
        this.canvas.enabled = screen != SharedSession.Screen.Game;
        this.startPanel.SetActive(screen == SharedSession.Screen.Start);
        this.lobbyPanel.SetActive(screen == SharedSession.Screen.Lobby);
        this.roomPanel.SetActive(screen == SharedSession.Screen.Room || screen == SharedSession.Screen.Loading);

        // 로비를 벗어난 경우 이전 방 생성 모달 숨김
        if (screen != SharedSession.Screen.Lobby)
        {
            this.createPopup.SetActive(false);
        }

        // 등록 완료와 시작 화면 이탈 시 닉네임 모달 닫기
        if (screen != SharedSession.Screen.Start || this.session.HasNickname)
        {
            this.nicknamePopup.SetActive(false);
        }

        this.nickname.interactable = idle && string.IsNullOrEmpty(this.session.Nickname);
        this.confirmNickname.interactable = idle;
        this.cancelNickname.interactable = idle;
        this.nicknameError.text = this.session.NicknameError;

        // 연결 메시지와 복구 버튼을 갱신하고 모달 뒤쪽 입력 차단
        this.status.text = this.session.Status;
        this.lobbyTitle.text = this.session.Nickname;
        this.online.interactable = idle && !this.nicknamePopup.activeSelf;
        this.quit.interactable = idle && !this.nicknamePopup.activeSelf;
        this.onlineLabel.text = this.session.Runner == null &&
            this.session.Status.Contains("종료") ? "다시 연결" : "온라인 플레이";
        this.lobbyControls.interactable = idle && !this.createPopup.activeSelf;
        this.back.interactable = idle;
        this.openCreate.interactable = idle && this.session.Runner != null;
        this.refresh.interactable = idle && this.session.RefreshRemaining <= 0f;
        this.refreshLabel.text = this.session.Runner == null ? "재시도" : "새로고침";
        this.create.interactable = idle && this.session.Runner != null;
        this.cancelCreate.interactable = idle;
        this.roomName.interactable = idle;
        this.createError.text = this.session.RoomError;

        // 퇴장과 시작 조건을 반영한 뒤 로컬 플레이어 권한에 맞춰 준비 버튼 갱신
        this.leave.interactable = idle && screen == SharedSession.Screen.Room;
        this.ready.interactable = false;
        this.startGame.interactable = idle && this.session.RoomState != null && this.session.RoomState.CanStart;
        // 로비 목록 갱신 중에는 숨겨진 대기실의 문자열과 슬롯 갱신 생략
        if (this.roomPanel.activeSelf)
        {
            RenderPlayers();
        }

        // 목록 수신 여부와 연결 상태에 따라 빈 목록 안내 선택
        this.emptyList.text = this.session.HasSessionList ? "참가 가능한 방이 없습니다." : (this.session.Runner == null &&
            idle ? "로비 연결에 실패했습니다. 재시도를 눌러 주세요." : "방 목록을 불러오는 중...");
    }


    // 최대 10개 고정 슬롯에 현재 활성 플레이어와 방장 및 준비 상태 표시
    private void RenderPlayers()
    {
        // 퇴장한 플레이어 표시가 남지 않도록 고정 슬롯을 대기 상태로 초기화
        for (int i = 0; i < this.playerSlots.Length; i++)
        {
            this.playerSlots[i].text = $"{i + 1:00}   참가 대기 중";
            this.playerSlotBackgrounds[i].color = this.playerSlotDefaultColors[i];
        }

        NetworkRunner runner = this.session.Runner;
        this.roomTitle.text = this.session.RoomTitle;
        this.readyLabel.text = "준비";
        int count = 0;

        // 준비 상태와 같은 Shared Mode의 현재 방장 식별자를 표시에도 사용
        PlayerRef master = runner != null && runner.IsRunning ? runner.GetMasterClient() : PlayerRef.None;

        // 실제 활성 참가자만 순회하며 수신된 플레이어 데이터와 결합
        if (runner != null && runner.IsRunning)
        {
            foreach (PlayerRef id in runner.ActivePlayers)
            {
                if (count >= this.playerSlots.Length)
                {
                    break;
                }

                // 자신의 닉네임은 즉시 표시하고 원격 정보 수신 전에는 임시 번호 대신 안내 표시
                string displayName = id == runner.LocalPlayer ? this.session.Nickname : "닉네임을 불러오는 중...";
                string hostBadge = id == master ? "   [방장]" : "";
                string label = displayName + hostBadge;

                if (this.session.Players.TryGetValue(id, out PlayerRoomState player) && player != null &&
                    player.Object != null && player.Object.IsValid)
                {
                    string syncedNickname = player.Nickname.ToString();

                    if (!string.IsNullOrWhiteSpace(syncedNickname))
                    {
                        displayName = syncedNickname;
                    }

                    label = displayName + hostBadge;

                    // 방장과 미수신 데이터는 원래 색상 유지, 유효한 참가자 준비 상태만 색상으로 표시
                    if (id != master)
                    {
                        this.playerSlotBackgrounds[count].color = player.IsReady ? ReadySlotColor : NotReadySlotColor;
                    }

                    // 자신의 슬롯에만 준비 버튼 상태와 소유 권한 반영
                    if (id == runner.LocalPlayer)
                    {
                        this.readyLabel.text = id == master ? "준비" : (player.IsReady ? "준비 취소" : "준비");
                        this.ready.interactable = id != master && !this.session.IsBusy &&
                            !this.session.IsSceneLoading &&
                            this.session.CurrentScreen == SharedSession.Screen.Room &&
                            player.Object.HasStateAuthority;
                    }
                }

                this.playerSlots[count].text = $"{count + 1:00}   {label}";
                count++;
            }
        }

        // 표시한 활성 참가자 수를 방 인원 안내에 전달
        this.playerCount.text = $"{count} / {SharedSession.MaxPlayers}명";
    }


    // 필요한 항목만 확장하는 방 목록 Prefab 재사용
    private void RenderSessions()
    {
        // 목록 공급 세션이 없는 경우 갱신 중단
        if (this.session == null)
        {
            return;
        }

        IReadOnlyList<SessionInfo> sessions = this.session.Sessions;

        // 최신 목록 크기만큼 기존 항목을 재사용하고 부족한 항목만 생성
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i == this.items.Count)
            {
                this.items.Add(Instantiate(this.roomItemPrefab, this.listContent));
            }

            this.items[i].gameObject.SetActive(true);
            this.items[i].Bind(sessions[i], OnJoin);
        }

        // 새 목록에서 빠진 항목은 파괴 대신 비활성화하여 다음 갱신에 재사용
        for (int i = sessions.Count; i < this.items.Count; i++)
        {
            this.items[i].gameObject.SetActive(false);
        }

        // 방이 없는 경우에만 빈 목록 안내 노출
        this.emptyList.gameObject.SetActive(sessions.Count == 0);
    }


    // 방 만들기 팝업 표시 및 입력 포커스 이동
    private void OpenCreate()
    {
        // 진행 중인 연결 작업이 있는 경우 모달 진입 차단
        if (this.session.IsBusy)
        {
            return;
        }

        // 모달 표시로 로비 입력을 잠근 뒤 방 이름 입력에 포커스 전달
        this.createPopup.SetActive(true);
        Render();
        this.roomName.Select();
        this.roomName.ActivateInputField();
    }


    // 입력한 방 이름을 유지하며 팝업 닫기
    private void CloseCreate()
    {
        // 방 생성 요청이 끝나기 전 모달 닫기 차단
        if (this.session.IsBusy)
        {
            return;
        }

        // 입력값은 보존하고 모달을 숨긴 뒤 로비 버튼 상태 복원
        this.createPopup.SetActive(false);
        Render();
    }


    // 온라인 버튼의 비동기 입력 전달
    private async void OnOnline()
    {
        // 확정 닉네임이 있으면 재사용하고 최초 온라인 진입 시 입력 모달 표시
        if (this.session.HasNickname)
        {
            await RunAsync(this.session.ConnectAsync());
            return;
        }

        this.nicknamePopup.SetActive(true);
        this.nickname.text = this.session.Nickname;
        Render();
        this.nickname.Select();
        this.nickname.ActivateInputField();
    }


    // 조합 중 한글 자모는 유지하고 금지된 닉네임 문자 입력 차단
    private char ValidateNicknameCharacter(string text, int index, char character)
    {
        // 붙여넣기와 키보드 입력에 동일한 문자 허용 규칙 적용
        return OnlineNameRules.IsNicknameCharacter(character) ? character : '\0';
    }


    // 닉네임 선점과 로비 연결 요청 전달
    private async void OnConfirmNickname()
    {
        // 중복 버튼과 Enter 요청은 세션의 작업 잠금에서 차단
        await RunAsync(this.session.RegisterNicknameAsync(this.nickname.text));
    }


    // 키보드 제출을 확인 버튼과 동일한 등록 흐름에 연결
    private void OnSubmitNickname(string input)
    {
        // 모달이 열려 있고 입력 가능한 동안에만 Enter 제출 허용
        if (this.nicknamePopup.activeSelf && !this.session.IsBusy)
        {
            OnConfirmNickname();
        }
    }


    // 등록 요청 전 닉네임 입력 취소
    private void CloseNickname()
    {
        // 선점 진행 중 모달 취소로 인한 숨은 등록 방지
        if (this.session.IsBusy)
        {
            return;
        }

        this.nicknamePopup.SetActive(false);
        Render();
    }


    // 종료 버튼의 비동기 입력 전달
    private async void OnQuit()
    {
        // 종료 버튼 요청을 연결 정리와 앱 종료 처리로 전달
        await RunAsync(this.session.QuitAsync());
    }


    // 뒤로 가기 버튼의 비동기 입력 전달
    private async void OnBack()
    {
        // 뒤로 가기 입력을 로비 연결 종료 작업으로 전달
        await RunAsync(this.session.BackAsync());
    }


    // 방 생성 버튼의 비동기 입력 전달
    private async void OnCreate()
    {
        // 입력 필드의 원본 이름을 세션 검증과 방 생성 작업에 전달
        await RunAsync(this.session.CreateRoomAsync(this.roomName.text));
    }


    // 방 참가 버튼의 비동기 입력 전달
    private async void OnJoin(string sessionName)
    {
        // 선택한 행의 내부 세션 식별자를 참가 요청에 전달
        await RunAsync(this.session.JoinRoomAsync(sessionName));
    }


    // 방 퇴장 버튼의 비동기 입력 전달
    private async void OnLeave()
    {
        // 퇴장 입력을 현재 방 종료와 로비 복귀 작업에 전달
        await RunAsync(this.session.LeaveRoomAsync());
    }


    // 새로고침 입력과 중복되지 않는 쿨다운 표시 예약
    private async void OnRefresh()
    {
        // 새로고침 요청을 시작하고 제한 시간 표시를 예약한 뒤 완료 대기
        Task request = this.session.RefreshAsync();
        EnsureRefreshCooldown();
        await RunAsync(request);
    }


    // UI 재활성화 시에도 남은 새로고침 제한 시간의 버튼 복원 예약
    private void EnsureRefreshCooldown()
    {
        // 남은 제한 시간이 있고 기존 대기가 없는 경우에만 복원 작업 예약
        if (this.refreshCooldown == null && this.session != null && this.session.RefreshRemaining > 0f)
        {
            this.refreshCooldown = StartCoroutine(WaitForRefresh());
        }
    }


    // 마지막 새로고침 후 5초 경과 시 버튼 복원
    private IEnumerator WaitForRefresh()
    {
        // 게임 시간 배율과 무관하게 남은 새로고침 제한 시간 대기
        while (this.session != null && this.session.RefreshRemaining > 0f)
        {
            yield return new WaitForSecondsRealtime(0.2f);
        }

        // 대기 핸들을 비운 뒤 최신 상태로 버튼 사용 가능 여부 재계산
        this.refreshCooldown = null;
        Render();
    }


    // UI 이벤트 경계의 비동기 예외 기록
    private async Task RunAsync(Task request)
    {
        // 전달된 UI 요청의 완료를 기다리고 실패 예외를 Console에 기록
        try
        {
            await request;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }
}
