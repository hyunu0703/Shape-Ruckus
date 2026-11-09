using Fusion;

/// <summary>
/// 개별 플레이어 식별 정보와 닉네임 및 준비 상태 동기화
/// </summary>
public sealed class PlayerRoomState : NetworkBehaviour
{
    // 플레이어 식별 정보와 닉네임 동기화
    [Networked]
    public PlayerRef Player { get; private set; }

    [Networked]
    public NetworkString<_32> Nickname { get; private set; }

    // 플레이어 준비 상태 동기화
    [Networked, OnChangedRender(nameof(OnReadyChanged))]
    public NetworkBool IsReady { get; private set; }

    // 로컬 데이터 등록과 해제에 사용하는 참조
    private SharedSession session;
    private PlayerRef registeredPlayer;


    // 로컬 스폰 이전의 초기 네트워크 데이터 설정
    public void Initialize(PlayerRef player)
    {
        // 로컬 입장 콜백에서 받은 식별자로 닉네임과 최초 미준비 데이터 구성
        Player = player;
        Nickname = $"Player {player.PlayerId}";
        IsReady = false;
    }


    // 네트워크 부착 완료 후 플레이어 사전에 등록
    public override void Spawned()
    {
        // 디스폰 이후에도 등록을 해제할 수 있도록 세션과 플레이어 식별자 보관
        this.session = SharedSession.Instance;
        this.registeredPlayer = Player;

        // 현재 연결과 일치하는 경우 세션의 플레이어 사전에 등록 요청
        if (this.session != null)
        {
            this.session.RegisterPlayer(this);
        }
    }


    // 디스폰 시 저장된 식별자로 기존 UI 데이터 제거
    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        // 네트워크 상태가 해제된 뒤에도 저장한 식별자로 이전 등록 제거
        if (this.session != null)
        {
            this.session.UnregisterPlayer(this.registeredPlayer, this);
        }

        // 이전 세션으로 후속 변경이 전달되지 않도록 참조 해제
        this.session = null;
    }


    // 자신의 유효한 State Authority 데이터만 변경
    public void ToggleReady()
    {
        // 방 대기실 상태와 작업 잠금 및 자신의 State Authority를 모두 검사
        if (this.session == null ||
            this.session.CurrentScreen != SharedSession.Screen.Room ||
            this.session.IsBusy ||
            this.session.IsSceneLoading ||
            Object == null ||
            !Object.IsValid ||
            !Object.HasStateAuthority ||
            Player != Runner.LocalPlayer)
        {
            return;
        }

        // 권한이 있는 준비 값만 반전하고 로컬 화면에 즉시 변경 통지
        IsReady = !IsReady;
        OnReadyChanged();
    }


    // 준비 상태 변화에 따른 전체 클라이언트 UI 갱신 요청
    private void OnReadyChanged()
    {
        // 복제된 준비 상태 변경을 연결된 세션의 UI 갱신 이벤트로 전달
        if (this.session != null)
        {
            this.session.NotifyChanged();
        }
    }
}
