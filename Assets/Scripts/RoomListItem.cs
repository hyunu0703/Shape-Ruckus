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
    [SerializeField] private TMP_Text _title;
    [SerializeField] private TMP_Text _count;
    [SerializeField] private Button _join;
    private string _sessionName;
    private Action<string> _joinRequested;

    // 활성 항목의 클릭 이벤트 연결
    private void OnEnable()
    {
        _join.onClick.AddListener(OnJoin);
    }

    // 풀 반납 항목의 이벤트와 이전 세션 참조 해제
    private void OnDisable()
    {
        _join.onClick.RemoveListener(OnJoin);
        _joinRequested = null;
        _sessionName = null;
    }

    // 최신 로비 스냅샷을 재사용 항목에 표시
    public void Bind(SessionInfo session, Action<string> joinRequested)
    {
        _sessionName = session.Name;
        _joinRequested = joinRequested;
        _title.text = SharedSession.GetTitle(session);
        _count.text = $"{session.PlayerCount} / {SharedSession.MaxPlayers}";
        _join.interactable = SharedSession.IsJoinable(session);
    }

    // 표시 이름과 분리된 내부 식별자로 참가 요청
    private void OnJoin()
    {
        _joinRequested?.Invoke(_sessionName);
    }
}
