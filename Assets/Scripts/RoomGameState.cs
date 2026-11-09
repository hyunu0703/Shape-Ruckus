using System;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Master Client 권한을 이어받는 방 전체 시작 상태와 Scene 전환 관리
/// </summary>
public sealed class RoomGameState : NetworkBehaviour, IStateAuthorityChanged
{
    // 방 전체 게임 시작 상태 동기화
    [Networked, OnChangedRender(nameof(OnRoomChanged))]
    public NetworkBool IsStarting { get; private set; }

    // 로컬 세션 참조
    private SharedSession session;

    // 방장 권한과 플레이어 준비 조건 조회
    public bool CanStart => this.session != null &&
        Object != null &&
        Object.IsValid &&
        Object.HasStateAuthority &&
        Runner.IsSharedModeMasterClient &&
        !IsStarting &&
        this.session.CurrentScreen == SharedSession.Screen.Room &&
        !this.session.IsBusy &&
        !this.session.IsSceneLoading &&
        this.session.AreAllPlayersReady();


    // Scene에 배치된 유일한 방 상태 객체 등록
    public override void Spawned()
    {
        // 현재 로컬 세션을 확보한 뒤 Scene의 방 전체 상태 객체로 등록
        this.session = SharedSession.Instance;

        if (this.session != null)
        {
            this.session.RegisterRoom(this);
        }
    }


    // 새 Master Client로의 State Authority 이전 통지
    public void StateAuthorityChanged()
    {
        // 이전받은 State Authority를 기준으로 방장 표시와 시작 버튼 재평가 요청
        OnRoomChanged();
    }


    // 네트워크 권한과 준비 조건 재검증 후 동기화 Scene 전환
    public async void TryStartGame(string gameScenePath)
    {
        // UI 버튼 상태와 별도로 방장 권한과 전원 준비 및 중복 시작 조건 재검사
        if (!CanStart)
        {
            return;
        }

        // 등록된 게임 Scene 경로를 Fusion 로딩에 필요한 빌드 인덱스로 변환
        int gameIndex = SceneUtility.GetBuildIndexByScenePath(gameScenePath);

        if (gameIndex < 0)
        {
            Debug.LogError("Build Profile의 게임 Scene 누락", this);
            return;
        }

        NetworkRunner runner = Runner;

        // 시작 상태 변경부터 Scene 요청까지 동일한 실패 복구 경계로 처리
        try
        {
            // 현재 참가자의 시작 상태를 잠그고 추가 참가와 로비 노출 차단
            IsStarting = true;
            runner.SessionInfo.IsOpen = false;
            runner.SessionInfo.IsVisible = false;
            OnRoomChanged();
            await runner.LoadScene(SceneRef.FromIndex(gameIndex), LoadSceneMode.Single);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);

            // 전환 실패 시 불완전한 방 재개 대신 연결 종료 후 재시도 제공
            if (runner != null && !runner.IsShutdown)
            {
                // async void 경계 밖으로 종료 실패 예외가 전파되지 않도록 기록
                try
                {
                    await runner.Shutdown();
                }
                catch (Exception shutdownException)
                {
                    Debug.LogException(shutdownException);
                }
            }
        }
    }


    // 시작 상태와 권한 변경을 로컬 UI에 전달
    private void OnRoomChanged()
    {
        // 복제 상태와 권한 변경을 세션에 전달하여 화면 전환과 입력 잠금 갱신
        if (this.session != null)
        {
            this.session.NotifyRoomChanged();
        }
    }
}
