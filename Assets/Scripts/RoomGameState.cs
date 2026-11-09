using System;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Master Client 권한을 이어받는 방 전체 시작 상태와 Scene 전환 관리
/// </summary>
public sealed class RoomGameState : NetworkBehaviour, IStateAuthorityChanged
{
    [Networked, OnChangedRender(nameof(OnRoomChanged))] public NetworkBool IsStarting { get; private set; }
    private SharedSession _session;
    public bool CanStart => _session != null && Object != null && Object.IsValid && Object.HasStateAuthority &&
        Runner.IsSharedModeMasterClient && !IsStarting && _session.CurrentScreen == SharedSession.Screen.Room &&
        !_session.IsBusy && !_session.IsSceneLoading && _session.AreAllPlayersReady();

    // Scene에 배치된 유일한 방 상태 객체 등록
    public override void Spawned()
    {
        _session = SharedSession.Instance;
        if (_session != null) { _session.RegisterRoom(this); }
    }

    // 새 Master Client로의 State Authority 이전 통지
    public void StateAuthorityChanged()
    {
        OnRoomChanged();
    }

    // 네트워크 권한과 준비 조건 재검증 후 동기화 Scene 전환
    public async void TryStartGame(string gameScenePath)
    {
        if (!CanStart) { return; }
        int gameIndex = SceneUtility.GetBuildIndexByScenePath(gameScenePath);
        if (gameIndex < 0)
        {
            Debug.LogError("Build Profile의 게임 Scene 누락", this);
            return;
        }
        IsStarting = true;
        Runner.SessionInfo.IsOpen = false;
        Runner.SessionInfo.IsVisible = false;
        OnRoomChanged();
        NetworkRunner runner = Runner;
        try { await runner.LoadScene(SceneRef.FromIndex(gameIndex), LoadSceneMode.Single); }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            // 전환 실패 시 불완전한 방 재개 대신 연결 종료 후 재시도 제공
            if (runner != null && !runner.IsShutdown) { await runner.Shutdown(); }
        }
    }

    // 시작 상태와 권한 변경을 로컬 UI에 전달
    private void OnRoomChanged()
    {
        if (_session != null) { _session.NotifyRoomChanged(); }
    }
}
