using Fusion;

/// <summary>
/// 개별 플레이어 식별 정보와 닉네임 및 준비 상태 동기화
/// </summary>
public sealed class PlayerRoomState : NetworkBehaviour
{
    [Networked] public PlayerRef Player { get; private set; }
    [Networked] public NetworkString<_32> Nickname { get; private set; }
    [Networked, OnChangedRender(nameof(OnReadyChanged))] public NetworkBool IsReady { get; private set; }
    private SharedSession _session;
    private PlayerRef _registeredPlayer;

    // 로컬 스폰 이전의 초기 네트워크 데이터 설정
    public void Initialize(PlayerRef player)
    {
        Player = player;
        Nickname = $"Player {player.PlayerId}";
        IsReady = false;
    }

    // 네트워크 부착 완료 후 플레이어 사전에 등록
    public override void Spawned()
    {
        _session = SharedSession.Instance;
        _registeredPlayer = Player;
        if (_session != null) { _session.RegisterPlayer(this); }
    }

    // 디스폰 시 저장된 식별자로 기존 UI 데이터 제거
    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (_session != null) { _session.UnregisterPlayer(_registeredPlayer, this); }
        _session = null;
    }

    // 자신의 유효한 State Authority 데이터만 변경
    public void ToggleReady()
    {
        if (_session == null || _session.CurrentScreen != SharedSession.Screen.Room ||
            _session.IsBusy || _session.IsSceneLoading || !Object.HasStateAuthority || Player != Runner.LocalPlayer) { return; }
        IsReady = !IsReady;
        OnReadyChanged();
    }

    // 준비 상태 변화에 따른 전체 클라이언트 UI 갱신 요청
    private void OnReadyChanged()
    {
        if (_session != null) { _session.NotifyChanged(); }
    }
}
