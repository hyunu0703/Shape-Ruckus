using System;
using System.Threading.Tasks;
using Fusion.Matchmaking;
using Fusion.Photon.Realtime;
using Photon.Realtime;

/// <summary>
/// 앱 실행 동안 유지하는 Photon 닉네임 전용 방의 원자적 선점과 해제
/// </summary>
public sealed class NicknameReservation : IDisposable
{
    // 게임 방 Runner와 독립적인 닉네임 점유 연결
    private readonly RealtimeClient client = new RealtimeClient();
    private readonly AsyncConfig config;
    private readonly ConnectionServiceScope service;
    private readonly IDisposable disconnected;
    private bool isDisposed;
    private bool isReleasing;

    // 점유 연결 상태와 예기치 않은 해제 통지
    public bool IsHeld => !this.isDisposed && this.client.InRoom;
    public event Action Lost;


    // Unity 메인 스레드에서 연결 서비스와 종료 콜백 준비
    public NicknameReservation()
    {
        // 지속 서비스 하나만 사용하여 비동기 요청별 중복 Service 호출 차단
        this.config = AsyncConfig.CreateUnityAsyncConfig();
        this.config.CreateServiceTask = false;
        this.service = new ConnectionServiceScope(this.client, AsyncConfig.CreateUnityAsyncConfig());
        this.disconnected = this.client.CallbackMessage.ListenManual<OnDisconnectedMsg>(message =>
        {
            if (!this.isDisposed && !this.isReleasing)
            {
                Lost?.Invoke();
            }
        });
    }


    // 동일 이름의 숨겨진 1인 방 생성 성공 시에만 닉네임 점유 허용
    public async Task<bool> AcquireAsync(string nickname)
    {
        // 모든 게임 버전에서 동일 KR 닉네임 공간 사용
        var settings = PhotonAppSettings.Global.AppSettings.GetCopy();
        settings.FixedRegion = "kr";
        settings.AppVersion = "shape-ruckus-nicknames-v1";

        // Fusion 앱의 클라이언트와 전송 및 암호화 기본 설정 적용
        this.client.SetupForFusion(settings);
        await this.client.ConnectUsingSettingsAsync(settings, this.config);

        // Create 전용 요청으로 동시 제출도 서버에서 한 명만 성공
        short result = await this.client.CreateAndJoinRoomAsync(new EnterRoomArgs
        {
            RoomName = OnlineNameRules.GetKey("sr-nickname-", nickname),
            RoomOptions = new RoomOptions
            {
                // Fusion 서버가 요구하는 SDK 버전별 플러그인 명시
                Plugins = new[] { RealtimeClientExtensions.FusionPluginName },
                MaxPlayers = 1,
                IsOpen = false,
                IsVisible = false,
                PlayerTtl = 0,
                EmptyRoomTtl = 0
            }
        }, throwOnError: false, config: this.config);

        if (result == ErrorCode.GameIdAlreadyExists)
        {
            return false;
        }

        if (result != ErrorCode.Ok)
        {
            throw new InvalidOperationException($"닉네임 점유 요청 실패: {result}");
        }

        return IsHeld;
    }


    // 정상 앱 종료 시 서버의 닉네임 점유 해제 완료 대기
    public async Task ReleaseAsync()
    {
        // 의도한 종료를 점유 상실 복구와 구분
        this.isReleasing = true;
        try
        {
            await this.client.DisconnectAsync(this.config);
        }
        finally
        {
            Dispose();
        }
    }


    // Scene 관리 객체 파괴와 앱 종료 시 잔류 연결 및 서비스 정리
    public void Dispose()
    {
        // 중복 해제와 파괴 이후 콜백 방지
        if (this.isDisposed)
        {
            return;
        }

        this.isDisposed = true;
        this.disconnected.Dispose();
        this.client.Disconnect();
        this.client.RealtimePeer.SendOutgoingCommands();
        this.service.Dispose();
        Lost = null;
    }
}
