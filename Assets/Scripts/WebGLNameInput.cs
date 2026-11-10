using System.Runtime.InteropServices;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>WebGL의 브라우저 입력창을 통한 이름 입력과 IME 조합 처리</summary>
public sealed class WebGLNameInput : MonoBehaviour, IPointerClickHandler
{
    // 선택 상태가 유지된 입력창의 재클릭 처리
    public void OnPointerClick(PointerEventData eventData)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (eventData.button == PointerEventData.InputButton.Left)
        {
            BeginEdit("");
        }
#endif
    }


    // WebGL 플레이어에서만 입력 보완 컴포넌트 부착
    public static void Attach(TMP_InputField field)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
    // 브라우저 입력창의 생성과 위치 및 문자열 동기화
        if (field.GetComponent<WebGLNameInput>() == null)
        {
            field.gameObject.AddComponent<WebGLNameInput>();
        }
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void SRNameOpen(string receiver, string value, string color, string background);
    [DllImport("__Internal")] private static extern void SRNameRect(string receiver, float x, float y, float width, float height, float fontSize);
    [DllImport("__Internal")] private static extern void SRNameSet(string receiver, string value);
    [DllImport("__Internal")] private static extern void SRNameClose(string receiver);

    // 입력창과 화면 좌표 계산에 필요한 참조
    private TMP_InputField field;
    private Canvas canvas;
    private GameObject receiver;
    private readonly Vector3[] corners = new Vector3[4];

    // 편집 기간과 이전 키보드 캡처 설정 보관
    private bool editing;
    private bool previousCapture;


    // TMP 키보드 처리 중지와 브라우저 콜백 수신 대상 생성
    private void Awake()
    {
        this.field = GetComponent<TMP_InputField>();
        this.canvas = this.field.GetComponentInParent<Canvas>();
        // TMP의 중복 입력과 자체 모바일 키보드 차단
        this.field.readOnly = true;
        this.field.shouldHideSoftKeyboard = true;
        this.field.onSelect.AddListener(BeginEdit);
        this.field.onDeselect.AddListener(EndEdit);
        // 이름 기반 SendMessage가 씬의 동명 UI와 충돌하지 않도록 고유 대상 생성
        this.receiver = new GameObject("WebGLNameInput_" + GetInstanceID());
        this.receiver.transform.SetParent(transform, false);
        this.receiver.AddComponent<WebGLNameInputReceiver>().Owner = this;
    }


    // 브라우저 입력에 키보드 제어 전달
    private void BeginEdit(string unused)
    {
        if (this.editing || !this.field.IsInteractable())
        {
            return;
        }

        this.editing = true;
        this.previousCapture = WebGLInput.captureAllKeyboardInput;
        WebGLInput.captureAllKeyboardInput = false;
        string color = "#" + ColorUtility.ToHtmlStringRGB(this.field.textComponent.color);
        string background = "#" + ColorUtility.ToHtmlStringRGB(this.field.targetGraphic.color);
        SRNameOpen(this.receiver.name, this.field.text, color, background);
        UpdateRect();
    }


    // 입력 잠금 반영과 화면 크기 변경 추적
    private void LateUpdate()
    {
        if (!this.editing)
        {
            return;
        }

        if (!this.field.IsInteractable())
        {
            EndEdit("");
        }
        else
        {
            UpdateRect();
        }
    }


    // TMP 텍스트 영역을 canvas 기준 비율로 브라우저에 전달
    private void UpdateRect()
    {
        Camera camera = this.canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : this.canvas.worldCamera;
        this.field.textViewport.GetWorldCorners(this.corners);
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, this.corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, this.corners[2]);
        SRNameRect(this.receiver.name, min.x / Screen.width, 1f - max.y / Screen.height,
            (max.x - min.x) / Screen.width, (max.y - min.y) / Screen.height,
            this.field.textComponent.fontSize * this.canvas.scaleFactor / Screen.height);
    }


    // 조합이 확정된 브라우저 문자열에 기존 문자 규칙과 길이 제한 적용
    internal void ReceiveValue(string value)
    {
        // text 대입에서 생략되는 TMP onValidateInput을 명시적으로 호출
        var accepted = new StringBuilder();

        foreach (char character in value)
        {
            char validated = this.field.onValidateInput == null ? character :
                this.field.onValidateInput(accepted.ToString(), accepted.Length, character);
            if (validated != '\0' && validated != '\r' && validated != '\n')
            {
                accepted.Append(validated);
            }

            if (this.field.characterLimit > 0 && accepted.Length >= this.field.characterLimit)
            {
                break;
            }
        }

        this.field.text = accepted.ToString();

        if (this.editing && this.field.text != value)
        {
            SRNameSet(this.receiver.name, this.field.text);
        }
    }


    // 최종 문자열 반영 후 포커스 해제와 필요한 제출 이벤트 전달
    internal void Finish(string value, bool submit)
    {
        ReceiveValue(value);
        EndEdit("");
        this.field.DeactivateInputField();

        if (submit)
        {
            this.field.onSubmit.Invoke(this.field.text);
        }
    }


    // 마지막 조합 문자 회수와 Unity 키보드 캡처 복원
    private void EndEdit(string unused)
    {
        if (!this.editing)
        {
            return;
        }

        // DOM 제거 전에 조합 중인 마지막 글자까지 동기적으로 회수
        SRNameClose(this.receiver.name);
        this.editing = false;
        WebGLInput.captureAllKeyboardInput = this.previousCapture;
    }


    // 팝업 닫기와 씬 전환 시 DOM 입력창 정리
    private void OnDisable()
    {
        EndEdit("");
    }


    // 수명 종료 시 TMP 이벤트 구독 해제
    private void OnDestroy()
    {
        this.field.onSelect.RemoveListener(BeginEdit);
        this.field.onDeselect.RemoveListener(EndEdit);
    }
#endif
}

