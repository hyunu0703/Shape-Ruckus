using UnityEngine;
using UnityEngine.Scripting;

/// <summary>브라우저 문자열 콜백 수신과 해당 이름 입력창으로 전달</summary>
[Preserve]
public sealed class WebGLNameInputReceiver : MonoBehaviour
{
#if UNITY_WEBGL && !UNITY_EDITOR
    internal WebGLNameInput Owner;


    // 조합 완료 문자열 전달과 IL2CPP stripping 방지
    [Preserve]
    public void OnBrowserInput(string value)
    {
        Owner.ReceiveValue(value);
    }


    // 포커스 이동 시 마지막 문자열 확정
    [Preserve]
    public void OnBrowserBlur(string value)
    {
        Owner.Finish(value, false);
    }


    // 조합 확정 이후 Enter 제출 전달
    [Preserve]
    public void OnBrowserSubmit(string value)
    {
        Owner.Finish(value, true);
    }
#endif
}
