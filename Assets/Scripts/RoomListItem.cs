using System;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 재사용 방 목록 항목의 표시 정보와 내부 세션 식별자 연결
/// </summary>
public sealed class RoomListItem : MonoBehaviour
{
    // 방 이름과 인원 및 참가 버튼 표시
    [SerializeField] private TMP_Text title;
    [SerializeField] private TMP_Text count;
    [SerializeField] private Button join;

    // 내부 세션 식별자와 참가 요청 전달
    private string sessionName;
    private Action<string> joinRequested;


    // 활성 항목의 클릭 이벤트 연결
    private void OnEnable()
    {
        // Prefab 참조 누락 시 부분 구성 상태의 클릭 구독 차단
        if (this.title == null || this.count == null || this.join == null)
        {
            Debug.LogError("RoomListItem의 필수 표시 참조 누락", this);
            enabled = false;
            return;
        }

        // 풀에서 활성화된 행의 참가 버튼을 현재 행 콜백에 연결
        this.join.onClick.AddListener(OnJoin);
    }


    // 풀 반납 항목의 이벤트와 이전 세션 참조 해제
    private void OnDisable()
    {
        // 클릭 구독과 이전 방 식별자를 함께 해제하여 재사용 시 오참가 방지
        if (this.join != null)
        {
            this.join.onClick.RemoveListener(OnJoin);
        }

        this.joinRequested = null;
        this.sessionName = null;
    }


    // 최신 로비 스냅샷을 재사용 항목에 표시
    public void Bind(SessionInfo session, Action<string> joinRequested)
    {
        // 잘못된 목록 데이터와 비활성 컴포넌트의 표시 참조 접근 차단
        if (!enabled || session == null)
        {
            return;
        }

        // 표시 이름과 구분되는 Photon 내부 세션 이름 및 요청 콜백 보관
        this.sessionName = session.Name;
        this.joinRequested = joinRequested;

        // 최신 목록 데이터로 이름과 인원을 표시하고 참가 조건 반영
        this.title.text = SharedSession.GetTitle(session);
        this.count.text = $"{session.PlayerCount} / {SharedSession.MaxPlayers}";
        this.join.interactable = SharedSession.IsJoinable(session);
    }


    // 표시 이름과 분리된 내부 식별자로 참가 요청
    private void OnJoin()
    {
        // 클릭한 행에 저장된 내부 이름을 상위 UI의 참가 처리에 전달
        this.joinRequested?.Invoke(this.sessionName);
    }
}
