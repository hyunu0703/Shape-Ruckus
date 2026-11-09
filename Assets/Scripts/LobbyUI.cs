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
    [SerializeField] private SharedSession _session;
    [SerializeField] private Canvas _canvas;
    [SerializeField] private GameObject _startPanel;
    [SerializeField] private GameObject _lobbyPanel;
    [SerializeField] private GameObject _roomPanel;
    [SerializeField] private GameObject _createPopup;
    [SerializeField] private CanvasGroup _lobbyControls;
    [SerializeField] private TMP_Text _status;
    [SerializeField] private TMP_Text _emptyList;
    [SerializeField] private RectTransform _listContent;
    [SerializeField] private RoomListItem _roomItemPrefab;
    [SerializeField] private TMP_InputField _roomName;
    [SerializeField] private TMP_Text _createError;
    [SerializeField] private TMP_Text _roomTitle;
    [SerializeField] private TMP_Text _playerCount;
    [SerializeField] private TMP_Text[] _playerSlots;
    [SerializeField] private TMP_Text _readyLabel;
    [SerializeField] private Button _online;
    [SerializeField] private Button _quit;
    [SerializeField] private Button _back;
    [SerializeField] private Button _refresh;
    [SerializeField] private Button _openCreate;
    [SerializeField] private Button _create;
    [SerializeField] private Button _cancelCreate;
    [SerializeField] private Button _ready;
    [SerializeField] private Button _startGame;
    [SerializeField] private Button _leave;
    private readonly List<RoomListItem> _items = new List<RoomListItem>();
    private Coroutine _refreshCooldown;

    // 필수 UI 참조 검증
    private void Awake()
    {
        if (_session == null || _canvas == null || _startPanel == null || _lobbyPanel == null ||
            _roomPanel == null || _roomItemPrefab == null || _listContent == null || _roomName == null ||
            _playerSlots == null || _playerSlots.Length != SharedSession.MaxPlayers)
        {
            Debug.LogError("LobbyUI의 필수 Scene 참조 누락", this);
            enabled = false;
        }
    }

    // 화면 활성 기간의 네트워크 및 버튼 이벤트 구독
    private void OnEnable()
    {
        if (_session == null) { return; }
        _session.Changed += Render;
        _session.SessionListChanged += RenderSessions;
        _online.onClick.AddListener(OnOnline);
        _quit.onClick.AddListener(OnQuit);
        _back.onClick.AddListener(OnBack);
        _refresh.onClick.AddListener(OnRefresh);
        _openCreate.onClick.AddListener(OpenCreate);
        _create.onClick.AddListener(OnCreate);
        _cancelCreate.onClick.AddListener(CloseCreate);
        _ready.onClick.AddListener(_session.ToggleReady);
        _startGame.onClick.AddListener(_session.StartMatch);
        _leave.onClick.AddListener(OnLeave);
        RenderSessions();
        Render();
        EnsureRefreshCooldown();
    }

    // 비활성화 시 구독과 단일 새로고침 대기 해제
    private void OnDisable()
    {
        if (_session != null)
        {
            _session.Changed -= Render;
            _session.SessionListChanged -= RenderSessions;
            _ready.onClick.RemoveListener(_session.ToggleReady);
            _startGame.onClick.RemoveListener(_session.StartMatch);
        }
        _online.onClick.RemoveListener(OnOnline);
        _quit.onClick.RemoveListener(OnQuit);
        _back.onClick.RemoveListener(OnBack);
        _refresh.onClick.RemoveListener(OnRefresh);
        _openCreate.onClick.RemoveListener(OpenCreate);
        _create.onClick.RemoveListener(OnCreate);
        _cancelCreate.onClick.RemoveListener(CloseCreate);
        _leave.onClick.RemoveListener(OnLeave);
        if (_refreshCooldown != null)
        {
            StopCoroutine(_refreshCooldown);
            _refreshCooldown = null;
        }
    }

    // 네트워크 상태에 따른 Panel과 입력 가능 상태 반영
    private void Render()
    {
        if (_session == null) { return; }
        SharedSession.Screen screen = _session.CurrentScreen;
        bool idle = !_session.IsBusy && !_session.IsSceneLoading;
        _canvas.enabled = screen != SharedSession.Screen.Game;
        _startPanel.SetActive(screen == SharedSession.Screen.Start);
        _lobbyPanel.SetActive(screen == SharedSession.Screen.Lobby);
        _roomPanel.SetActive(screen == SharedSession.Screen.Room || screen == SharedSession.Screen.Loading);
        if (screen != SharedSession.Screen.Lobby) { _createPopup.SetActive(false); }
        _status.text = _session.Status;
        _online.interactable = idle;
        _quit.interactable = idle;
        _online.GetComponentInChildren<TMP_Text>().text = _session.Runner == null && _session.Status.Contains("종료") ? "다시 연결" : "온라인 플레이";
        _lobbyControls.interactable = idle && !_createPopup.activeSelf;
        _back.interactable = idle;
        _openCreate.interactable = idle && _session.Runner != null;
        _refresh.interactable = idle && _session.RefreshRemaining <= 0f;
        _refresh.GetComponentInChildren<TMP_Text>().text = _session.Runner == null ? "재시도" : "새로고침";
        _create.interactable = idle && _session.Runner != null;
        _cancelCreate.interactable = idle;
        _roomName.interactable = idle;
        _createError.text = _session.RoomError;
        _leave.interactable = idle && screen == SharedSession.Screen.Room;
        _ready.interactable = false;
        _startGame.interactable = idle && _session.RoomState != null && _session.RoomState.CanStart;
        RenderPlayers();
        _emptyList.text = _session.HasSessionList ? "참가 가능한 방이 없습니다." :
            (_session.Runner == null && idle ? "로비 연결에 실패했습니다. 재시도를 눌러 주세요." : "방 목록을 불러오는 중...");
    }

    // 최대 10개 고정 슬롯에 현재 활성 플레이어와 방장 및 준비 상태 표시
    private void RenderPlayers()
    {
        for (int i = 0; i < _playerSlots.Length; i++) { _playerSlots[i].text = $"{i + 1:00}   참가 대기 중"; }
        NetworkRunner runner = _session.Runner;
        _roomTitle.text = _session.RoomTitle;
        _readyLabel.text = "준비";
        int count = 0;
        PlayerRef master = _session.RoomState != null && _session.RoomState.Object.IsValid
            ? _session.RoomState.Object.StateAuthority : PlayerRef.None;
        if (runner != null && runner.IsRunning)
        {
            foreach (PlayerRef id in runner.ActivePlayers)
            {
                if (count >= _playerSlots.Length) { break; }
                string label = $"Player {id.PlayerId}   데이터 수신 중...";
                if (_session.Players.TryGetValue(id, out PlayerRoomState player) && player != null && player.Object.IsValid)
                {
                    label = $"{player.Nickname}   {(id == master ? "[방장]   " : "")}{(player.IsReady ? "준비 완료" : "미준비")}";
                    if (id == runner.LocalPlayer)
                    {
                        label += "   (나)";
                        _readyLabel.text = player.IsReady ? "준비 취소" : "준비";
                        _ready.interactable = !_session.IsBusy && !_session.IsSceneLoading && _session.CurrentScreen == SharedSession.Screen.Room && player.Object.HasStateAuthority;
                    }
                }
                _playerSlots[count].text = $"{count + 1:00}   {label}";
                count++;
            }
        }
        _playerCount.text = $"{count} / {SharedSession.MaxPlayers}명";
    }

    // 필요한 항목만 확장하는 방 목록 Prefab 재사용
    private void RenderSessions()
    {
        if (_session == null) { return; }
        IReadOnlyList<SessionInfo> sessions = _session.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i == _items.Count) { _items.Add(Instantiate(_roomItemPrefab, _listContent)); }
            _items[i].gameObject.SetActive(true);
            _items[i].Bind(sessions[i], OnJoin);
        }
        for (int i = sessions.Count; i < _items.Count; i++) { _items[i].gameObject.SetActive(false); }
        _emptyList.gameObject.SetActive(sessions.Count == 0);
    }

    // 방 만들기 팝업 표시 및 입력 포커스 이동
    private void OpenCreate()
    {
        if (_session.IsBusy) { return; }
        _createPopup.SetActive(true);
        Render();
        _roomName.Select();
        _roomName.ActivateInputField();
    }

    // 입력한 방 이름을 유지하며 팝업 닫기
    private void CloseCreate()
    {
        if (_session.IsBusy) { return; }
        _createPopup.SetActive(false);
        Render();
    }

    // 온라인 버튼의 비동기 입력 전달
    private async void OnOnline() { await RunAsync(_session.ConnectAsync()); }
    // 종료 버튼의 비동기 입력 전달
    private async void OnQuit() { await RunAsync(_session.QuitAsync()); }
    // 뒤로 가기 버튼의 비동기 입력 전달
    private async void OnBack() { await RunAsync(_session.BackAsync()); }
    // 방 생성 버튼의 비동기 입력 전달
    private async void OnCreate() { await RunAsync(_session.CreateRoomAsync(_roomName.text)); }
    // 방 참가 버튼의 비동기 입력 전달
    private async void OnJoin(string sessionName) { await RunAsync(_session.JoinRoomAsync(sessionName)); }
    // 방 퇴장 버튼의 비동기 입력 전달
    private async void OnLeave() { await RunAsync(_session.LeaveRoomAsync()); }

    // 새로고침 입력과 중복되지 않는 쿨다운 표시 예약
    private async void OnRefresh()
    {
        Task request = _session.RefreshAsync();
        EnsureRefreshCooldown();
        await RunAsync(request);
    }

    // UI 재활성화 시에도 남은 새로고침 제한 시간의 버튼 복원 예약
    private void EnsureRefreshCooldown()
    {
        if (_refreshCooldown == null && _session != null && _session.RefreshRemaining > 0f)
        {
            _refreshCooldown = StartCoroutine(WaitForRefresh());
        }
    }

    // 마지막 새로고침 후 5초 경과 시 버튼 복원
    private IEnumerator WaitForRefresh()
    {
        while (_session != null && _session.RefreshRemaining > 0f) { yield return new WaitForSecondsRealtime(0.2f); }
        _refreshCooldown = null;
        Render();
    }

    // UI 이벤트 경계의 비동기 예외 기록
    private async Task RunAsync(Task request)
    {
        try { await request; }
        catch (Exception exception) { Debug.LogException(exception); }
    }
}
