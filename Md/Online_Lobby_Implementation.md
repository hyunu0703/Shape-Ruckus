# 온라인 로비와 방 대기실

## 현재 UI 기준: 480×854 세로 화면

MainMenu와 GameTest의 Canvas Scaler 기준 해상도를 480×854로 변경했다. Scale With Screen Size와 Match Width Or Height 0.5는 유지한다. 시작 버튼은 넓은 세로 배치, 로비는 상단 제목/리전과 중앙 Scroll View 및 하단 버튼, 대기실은 10명 단일 열과 하단 준비/시작/퇴장 버튼으로 구성했다. 방 목록 Prefab은 높이 96으로 표시 이름과 인원을 왼쪽에, 참가 버튼을 오른쪽에 배치했다. 생성 모달과 TMP 글자 크기도 세로 화면에 맞췄다.

Scene 및 Prefab의 오브젝트 ID와 직렬화 참조를 보존했고 37개 변경 영역의 부모 경계를 정적 검사했다. 이번 UI 변경의 Unity 실행 및 빌드 검증은 수행하지 않았다. 이전 1920×1080/1280×720 검증 결과는 변경 이전 UI에 대한 기록이다. 아래 Editor 도구 설명은 이전 구현 당시의 기록이며 사용자가 삭제한 Assets/Editor 폴더는 다시 생성하지 않았다.

## 시작 방법

`Assets/Scenes/MainMenu.unity`를 열고 Play를 누른다. 온라인 플레이 버튼을 눌러야 Photon 연결을 시작한다. 활성 Build Profile의 전역 Scene 목록은 MainMenu(0), GameTest(1) 순서다. 기존 SampleScene의 카메라와 2D 조명을 재사용했고 열려 있던 Scene의 저장되지 않은 변경은 저장하여 보존했다.

기존 PhotonAppSettings의 App ID를 유지하고 FixedRegion을 `kr`로 설정했다. 연결마다 설정 복사본에도 `kr`를 적용한다. App ID를 스크립트에 복사하지 않는다.

## 구성

| 파일 | 책임 |
| --- | --- |
| SharedSession | 별도 Runner 생성/종료, Shared 로비, 방 목록, 생성/참가/퇴장, 복구, PlayerRef 사전 |
| LobbyUI | 시작/로비/대기실 Panel, 생성 모달, 10개 플레이어 슬롯, 재사용 방 목록 |
| RoomListItem | 표시 이름/인원과 내부 SessionName 연결 |
| PlayerRoomState | 플레이어 식별자, 닉네임, 자신의 준비 상태 동기화 |
| RoomGameState | Scene의 Master Client Object, 시작 조건 재검사, 게임 시작 상태, Fusion Scene 전환 |

런타임 스크립트는 5개다. 중복 네트워크 Manager나 추가 Interface/Service는 만들지 않았다. Update나 전체 오브젝트 반복 검색, LINQ 정렬은 없다. 목록은 Photon 콜백마다 11개 인원 그룹으로 분류하여 O(n)으로 교체하고 동률의 수신 순서를 보존한다. 방 목록 항목은 Prefab을 재사용한다. 전체 준비 상태는 현재 ActivePlayers를 기준으로 O(n) 검사한다.

SharedSession과 UI는 Scene 전환에 유지되고 Runner는 별도 영속 GameObject다. 로비에서 방에 진입할 때 Runner를 재사용하며 퇴장/새로고침/실패 복구에는 새 Runner를 사용한다. 최초 MainMenu 네트워크 등록과 실제 게임 Scene 전환을 구분하고 Scene 로딩 중 입력을 잠근다.

MainMenu의 RoomGameState는 `MasterClientObject` 플래그를 사용하고 퇴장 시 파괴 플래그를 해제했다. 새 방장에게 State Authority가 자동 이전된다. PlayerRoomState Prefab에는 `DestroyWhenStateAuthorityLeaves`를 설정했다. 각 클라이언트는 로컬 입장에서만 하나의 플레이어 객체를 스폰한다.

게임 시작은 방장, State Authority, 2명 이상, 현재 ActivePlayers 전원의 유효한 플레이어 데이터와 준비 상태, 미시작/미로딩 조건을 네트워크 로직에서 다시 검사한다. 방을 닫고 목록에서 숨긴 뒤 `NetworkRunner.LoadScene`과 `NetworkSceneManagerDefault`로 GameTest에 진입한다.

## 폰트

Noto Sans KR Regular 원본 OTF와 동적 다중 아틀라스 TMP 에셋을 사용한다. 원본의 OFL 1.1 라이선스와 저작권 고지를 Assets/Fonts와 StreamingAssets/ThirdParty에 포함했다. 기본 UI는 Unity Image/Button/Panel과 설치된 uGUI의 TMP 필수 리소스다.

## 재실행 도구

- `Shape Ruckus > Build Lobby Assets`: Scene, Prefab, 폰트 참조와 Build Scene 등록 재생성
- `Shape Ruckus > Verify Lobby`: 입력 경계, 에셋 설정, 단일 Editor 실서버 흐름과 두 해상도 검사
- `Shape Ruckus > Build WebGL Verification`: 기존 압축 설정을 보존하며 `Builds/LobbyWebGL`에 로컬 검증 빌드 생성

에셋 재생성은 생성된 MainMenu/GameTest/Prefab 구성을 다시 작성한다. UI를 직접 편집한 뒤에는 재생성 대신 일반 Play를 사용한다. 검증 결과와 스크린샷은 Git에서 제외된 Logs에 기록한다. 빌드 산출물도 Git에서 제외된 Builds에 기록한다. Cloudflare 배포나 기존 web 폴더 교체는 실행하지 않는다.

## 실제 수행한 검증

- Unity 6000.3.23f1, Fusion 2.1.4.2500 컴파일 및 IL weaving 성공
- 시작 화면과 자동 연결 없음, 연결 즉시 중복 요청 차단
- 실제 Photon Shared 로비 접속 및 kr 방 생성
- 로비에서 사용한 Runner의 방 생성 재사용
- 공백/1글자/21글자/개행/제어문자/유니코드 줄 구분자 거절, 20글자 허용, Trim
- 내부 sr-GUID 세션과 표시 이름 분리
- 생성자 데이터와 Scene 방 상태 등록, 로컬 State Authority, 최초 미준비
- 로컬 준비/취소 및 준비한 1명 방의 UI/네트워크 시작 거절
- 퇴장 후 새 Runner로 로비 복귀, 이전 플레이어 데이터 제거
- 새로고침 새 Runner 사용 및 5초 중복 요청 차단
- 뒤로 가기 후 시작 화면/Runner 참조 정리
- 시작/로비/대기실의 1920×1080 및 1280×720 버튼 경계 검사와 스크린샷 확인
- 최종 단일 클라이언트 실행 검증에서 Console Error/Exception 없음

## 추가 검증

WebGL 빌드 1회는 성공했으며 오류와 경고는 모두 0개다. 이후 사용자 요청으로 추가 빌드 및 브라우저 테스트를 중단했다. 로컬 테스트 HTTP 서버도 종료했다. 검증 빌드에 사용한 압축 설정은 기존 값으로 복원했다.

미검증 항목:

- 독립 Editor/WebGL 상호 참가 및 두 클라이언트의 SessionInfo.Name 일치
- 원격 준비 상태 복제와 방장 퇴장 후 권한 이전 및 준비 유지
- 전원 준비 후 모든 참가자의 GameTest 동기화 전환 및 목록에서 제거
- 존재하지 않는 방 참가 실패와 연결 실패/예상하지 못한 종료의 실제 복구
- 다수 방 목록의 실제 서버 필터/동률 정렬 및 대량 Prefab 재사용
- 방 생성 모달의 두 해상도 배치와 생성 실패 시 입력 보존
- UI 비활성화/재활성화 중 새로고침 제한 시간 복원
- 10명 동시 접속 및 Photon 계정 플랜 CCU 제한 확인

마지막 정적 검토에서 UI 재활성화 시 남은 새로고침 대기를 복원하고, 플레이어 객체 진입점에서도 비동기 작업/Scene 로딩 중 준비 변경을 차단했다. 이 보완 이후 추가 실행 검증은 사용자 요청에 따라 수행하지 않았다. 직접 실행하지 못한 항목은 성공으로 처리하지 않는다.

## Photon 공식 근거

- [Matchmaking: Sessions, Lobbies & Rooms](https://doc.photonengine.com/fusion/v2/manual/connection-and-matchmaking/matchmaking)
- [Shared Mode Master Client](https://doc.photonengine.com/fusion/v2/manual/shared-mode-master-client)
- [NetworkObject](https://doc.photonengine.com/fusion/v2/manual/network-object)
- [PlayerRef](https://doc.photonengine.com/fusion/v2/manual/playerref)
- [Scene Loading](https://doc.photonengine.com/fusion/v2/manual/scene-loading)
- [Change Detection](https://doc.photonengine.com/fusion/v2/manual/data-transfer/change-detection)

네트워크 동작은 Photon 공식 문서와 설치된 Photon SDK의 코드 및 API XML을 확인했다. 신규 스크립트는 Unity_CSharp_Coding_Rules.md를 적용했고 필수 외부 콜백 계약에만 미사용 빈 메서드를 남겼다. 기존 SharedSession에는 외부에서 호출하는 public API가 없었다.
