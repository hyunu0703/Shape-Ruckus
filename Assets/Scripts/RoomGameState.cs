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

    // SetMasterClient 요청이 반영되기 전 같은 대상에게 중복 요청 방지
    private PlayerRef requestedMaster;
    private PlayerRef renderedMaster;
    private bool renderedMasterSettled;

    // Fusion이 임시로 지정한 방장의 승계가 끝난 뒤에만 준비와 시작 허용
    public bool IsMasterSettled => Runner != null && Runner.IsRunning &&
        Runner.GetMasterClient() != PlayerRef.None && Runner.GetMasterClient() == GetNextMaster();

    // 방장 권한과 플레이어 준비 조건 조회
    public bool CanStart => this.session != null &&
        Object != null &&
        Object.IsValid &&
        Object.HasStateAuthority &&
        Runner.IsSharedModeMasterClient &&
        IsMasterSettled &&
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
        this.requestedMaster = PlayerRef.None;
        // 이전받은 State Authority를 기준으로 방장 표시와 시작 버튼 재평가 요청
        OnRoomChanged();
    }


    // 콜백과 권한 이전의 도착 순서에 관계없이 현재 방장이 최종 승계를 완료
    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority || !Runner.IsSharedModeMasterClient)
        {
            return;
        }

        PlayerRef next = GetNextMaster();
        if (next == PlayerRef.None || next == Runner.LocalPlayer)
        {
            this.requestedMaster = PlayerRef.None;
            return;
        }

        if (this.requestedMaster != next)
        {
            Runner.SetMasterClient(next);
            this.requestedMaster = next;
        }
    }


    // Fusion의 방장 지정과 객체 권한 변경이 서로 다른 시점에 도착해도 UI 갱신
    public override void Render()
    {
        PlayerRef master = Runner.GetMasterClient();
        bool settled = IsMasterSettled;
        if (this.renderedMaster != master || this.renderedMasterSettled != settled)
        {
            this.renderedMaster = master;
            this.renderedMasterSettled = settled;
            OnRoomChanged();
        }
    }


    // Fusion 2의 재사용되지 않는 PlayerRef는 실제 입장 순서이며 UI 정렬과 무관
    private PlayerRef GetNextMaster()
    {
        PlayerRef next = PlayerRef.None;
        foreach (PlayerRef player in Runner.ActivePlayers)
        {
            if (next == PlayerRef.None || player.RawEncoded < next.RawEncoded)
            {
                next = player;
            }
        }

        return next;
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
