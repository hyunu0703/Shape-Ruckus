using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// 온라인 이름의 한국어와 영어 금칙어 및 기본 우회 표기 검사
/// </summary>
public static class OnlineNameFilter
{
    // LDNOOBW 영어와 한국어 목록에서 선별 및 변형 추가
    // 출처와 변경 사항 및 CC BY 4.0 고지는 StreamingAssets/ThirdParty/OnlineNameFilter.txt 참조
    private static readonly string[] BlockedParts =
    {
        "씨발", "씨팔", "씨벌", "씨부랄", "씨부럴", "시팔", "시벌", "시부랄", "시부럴",
        "개새끼", "개자식", "개좆", "계집년", "니기미", "뒤질래", "딸딸이", "또라이",
        "미친새끼", "병신", "빠구리", "사까시", "섹스", "쎅스", "쌍놈", "씹새끼",
        "씹빨", "씹알", "씹창", "씹팔", "씹물", "잡년", "종간나", "죽일년", "쥐좆",
        "좆", "짱깨", "쪽바리", "창녀", "포르노", "화냥년", "후레아들", "후장",
        "염병", "옘병", "지랄", "개년", "썅", "염창", "좇같", "좃", "자살해",
        "야동", "야애니", "은꼴", "강간", "성폭행", "성매매", "조건만남", "원조교제",
        "성관계", "성행위", "성교", "사정액", "정액", "음경", "음핵", "음란", "음모노출",
        "페니스", "클리토리스", "오르가즘", "딜도", "애널", "항문섹", "노출방", "누드",
        "fuck", "shit", "bitch", "asshole", "arsehole", "assmunch", "bastard", "bollocks",
        "blowjob", "blow job", "handjob", "hand job", "footjob", "cocksucker", "motherfucker",
        "faggot", "nigger", "nigga", "dildo", "cumshot", "cumming", "creampie",
        "cunnilingus", "fellatio", "ejaculat", "masturbat", "gangbang", "gang bang", "incest",
        "hentai", "horny", "porn", "pussy", "orgasm", "orgy", "pedophil", "paedophil",
        "penis", "vagina", "clitoris", "vulva", "semen", "sodomy", "sodomiz", "bestiality",
        "beastiality", "zoophil", "necrophil", "bondage", "bdsm", "bukkake", "futanari",
        "rimjob", "butthole", "bunghole", "wank", "whore", "slut", "jizz", "titties", "titty",
        "sexcam", "sexchat", "livesex", "cybersex", "sexual", "sexy", "threesome", "fisting",
        "dominatrix", "femdom", "strapon", "strap on", "upskirt", "naked", "nudity", "nudes"
    };

    // 정상 단어 내부와 겹치기 쉬운 표현은 전체 이름 또는 분리된 단어만 차단
    private static readonly string[] WordList =
    {
        "시발", "미친", "보지", "자지", "자위", "불알", "씹", "근친", "변태", "고자",
        "거유", "유두", "성기", "에로", "호로", "암캐", "야사", "ㅅㅂ", "ㅆㅂ", "ㅂㅅ", "ㅈㄹ", "ㅅㅅ",
        "ass", "arse", "anal", "anus", "cock", "cocks", "dick", "cum", "cunt", "sex", "tit", "tits",
        "boob", "boobs", "rape", "rapist", "nude", "milf", "fag", "smut", "twat", "erotic"
    };
    private static readonly HashSet<string> BlockedWords = BuildWords(WordList);
    private static readonly HashSet<string> JamoWords = BuildWords(WordList, true);
    private static readonly string[] PartKeys = BuildParts();
    private static readonly string[] JamoPartKeys = BuildParts(true);

    // 한글 초성 중성 종성을 호환 자모 하나의 표기로 통합
    private const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private const string Vowels = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    private const string Finals = "ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";


    // 제출 이름의 금칙어와 공백 기호 및 숫자 치환 우회 검사
    public static bool ContainsBlockedWord(string value)
    {
        // 원본과 일반적인 영문 숫자 치환 표기를 각각 검사
        foreach (bool substitutions in new[] { false, true })
        {
            string key = BuildKey(value, substitutions);

            foreach (string part in PartKeys)
            {
                if (key.IndexOf(part, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            if (BlockedWords.Contains(key.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')))
            {
                return true;
            }

            // 짧은 영단어의 정상 이름 내부 오탐 방지를 위한 언어와 구분자별 검사
            var token = new StringBuilder();
            int previousGroup = 0;

            foreach (char character in value.Normalize(NormalizationForm.FormKC) + " ")
            {
                int group = character <= 127 && char.IsLetterOrDigit(character) ? 1 :
                    (OnlineNameRules.IsHangul(character) ? 2 : 0);

                if (group == 0 || (previousGroup != 0 && previousGroup != group))
                {
                    string word = BuildKey(token.ToString().Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'), substitutions);

                    if (BlockedWords.Contains(word))
                    {
                        return true;
                    }

                    token.Clear();
                }

                if (group != 0)
                {
                    token.Append(character);
                }

                previousGroup = group;
            }
        }

        // 명시적으로 분리해 입력한 한글 자모만 추가 검사하여 일반 음절 내부 오탐 방지
        var jamoRun = new StringBuilder();

        foreach (char character in value + "가")
        {
            if (OnlineNameRules.IsHangul(character) && !(character >= '\uac00' && character <= '\ud7a3'))
            {
                jamoRun.Append(character);
            }
            else if (char.IsLetterOrDigit(character))
            {
                string jamoKey = BuildKey(jamoRun.ToString(), false, true);

                if (JamoWords.Contains(jamoKey))
                {
                    return true;
                }

                foreach (string part in JamoPartKeys)
                {
                    if (jamoKey.IndexOf(part, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }

                jamoRun.Clear();
            }
        }

        return false;
    }


    // 전각 문자와 한글 조합을 통일하고 필터 우회용 구분 문자 제거
    private static string BuildKey(string value, bool substitutions, bool decompose = false)
    {
        // 표시 이름과 별개로 비교 전용 문자열 생성
        var key = new StringBuilder();

        string normalized = value.Normalize(decompose ? NormalizationForm.FormKD : NormalizationForm.FormKC).ToLowerInvariant();

        foreach (char original in normalized)
        {
            char character = original;

            if (decompose && character >= '\u1100' && character <= '\u1112')
            {
                character = Initials[character - '\u1100'];
            }
            else if (decompose && character >= '\u1161' && character <= '\u1175')
            {
                character = Vowels[character - '\u1161'];
            }
            else if (decompose && character >= '\u11a8' && character <= '\u11c2')
            {
                character = Finals[character - '\u11a8'];
            }

            if (substitutions)
            {
                switch (character)
                {
                    case '0': character = 'o'; break;
                    case '1': character = 'i'; break;
                    case '3': character = 'e'; break;
                    case '4': character = 'a'; break;
                    case '5': character = 's'; break;
                    case '7': character = 't'; break;
                    case '@': character = 'a'; break;
                    case '$': character = 's'; break;
                    case '!': character = 'i'; break;
                }
            }

            if (char.IsLetterOrDigit(character) && char.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                key.Append(character);
            }
        }

        return key.ToString();
    }


    // 포함 검사 목록을 비교 전용 표기로 한 번만 변환
    private static string[] BuildParts(bool decompose = false)
    {
        // 런타임 요청마다 목록 정규화를 반복하지 않도록 캐시 생성
        var keys = new string[BlockedParts.Length];

        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = BuildKey(BlockedParts[i], false, decompose);
        }

        return keys;
    }


    // 단어 검사 목록을 중복 없는 정규화 사전으로 변환
    private static HashSet<string> BuildWords(string[] words, bool decompose = false)
    {
        // 대소문자와 한글 자모 조합을 동일한 비교 키로 저장
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (string word in words)
        {
            keys.Add(BuildKey(word, false, decompose));
        }

        return keys;
    }
}
