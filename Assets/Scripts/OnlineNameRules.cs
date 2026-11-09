using System;
using System.Text;

/// <summary>
/// 온라인 닉네임과 방 이름의 입력 검증 및 서버 식별자 생성
/// </summary>
public static class OnlineNameRules
{
    // UI와 네트워크 요청에서 공유하는 최대 길이
    public const int MaxNicknameLength = 12;
    public const int MaxRoomTitleLength = 24;


    // 입력 정규화와 이름별 길이 및 제어문자 검사
    public static bool TryValidate(string input, bool nickname, out string value, out string error)
    {
        // 원본 개행과 제어문자를 Trim 이전에 차단
        value = "";
        error = "";

        foreach (char character in input ?? "")
        {
            if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
            {
                error = "개행과 제어문자는 사용할 수 없습니다.";
                return false;
            }
        }

        // 잘못된 UTF-16 입력도 정규화 예외 대신 입력 오류로 전달
        try
        {
            value = (input ?? "").Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            error = "올바른 문자를 입력해 주세요.";
            return false;
        }

        // 닉네임은 원본의 앞뒤 공백까지 거절하고 한글 영문 숫자만 허용
        if (nickname)
        {
            foreach (char character in input ?? "")
            {
                if (!IsNicknameCharacter(character))
                {
                    error = "닉네임은 공백과 특수문자 없이 한글, 영문, 숫자만 사용할 수 있습니다.";
                    return false;
                }
            }
        }

        // 닉네임 1글자와 기존 방 이름 최소 2글자 조건 적용
        int minimum = nickname ? 1 : 2;
        int maximum = nickname ? MaxNicknameLength : MaxRoomTitleLength;

        if (value.Length < minimum || value.Length > maximum)
        {
            error = nickname ? $"닉네임은 {minimum}~{maximum}글자로 입력해 주세요." :
                $"방 이름은 앞뒤 공백을 제외하고 {minimum}~{maximum}글자로 입력해 주세요.";
            return false;
        }

        // 닉네임과 방 이름의 공통 금칙어 검사
        if (OnlineNameFilter.ContainsBlockedWord(value))
        {
            error = "욕설 또는 성적인 표현이 포함된 이름은 사용할 수 없습니다.";
            return false;
        }

        return true;
    }


    // 한글 조합 중 자모를 포함한 닉네임 입력 가능 문자 검사
    public static bool IsNicknameCharacter(char character)
    {
        // ASCII 영문 숫자와 한글만 허용
        return (character >= 'a' && character <= 'z') ||
            (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') || IsHangul(character);
    }


    // 완성형 한글과 IME 조합에 사용하는 자모 범위 검사
    public static bool IsHangul(char character)
    {
        // 비표시 채움 문자를 제외한 현대 한글과 호환 자모 허용
        return (character >= '\uac00' && character <= '\ud7a3') ||
            (character >= '\u1100' && character <= '\u1112') ||
            (character >= '\u1161' && character <= '\u1175') ||
            (character >= '\u11a8' && character <= '\u11c2') ||
            (character >= '\u3131' && character <= '\u3163');
    }


    // 정규화된 이름을 대소문자 구분 없는 충돌 없는 Photon 키로 변환
    public static string GetKey(string prefix, string value)
    {
        // 해시 충돌 없이 UTF-8 전체를 인코딩하여 동일 이름에 동일 세션 배정
        return prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC).ToUpperInvariant()));
    }
}
