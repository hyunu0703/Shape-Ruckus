mergeInto(LibraryManager.library, {
    $SRNameInput: {
        active: null,
        send: function (state, method) {
            if (SRNameInput.active !== state) return;
            SendMessage(state.receiver, method, state.input.value.normalize('NFC'));
        },
        close: function (state) {
            if (!state || SRNameInput.active !== state) return;
            SRNameInput.send(state, 'OnBrowserInput');
            SRNameInput.active = null;
            state.input.remove();
        }
    },
    SRNameOpen__deps: ['$SRNameInput'],
    SRNameOpen: function (receiver, value, color, background) {
        SRNameInput.close(SRNameInput.active);
        var input = document.createElement('input');
        var state = { receiver: UTF8ToString(receiver), input: input, composing: false };
        SRNameInput.active = state;
        input.type = 'text';
        input.value = UTF8ToString(value);
        input.lang = 'ko';
        input.autocomplete = 'off';
        input.spellcheck = false;
        input.setAttribute('aria-label', '이름 입력');
        Object.assign(input.style, {
            position: 'fixed', zIndex: '2147483647', boxSizing: 'border-box',
            border: '0', borderRadius: '0', outline: 'none', margin: '0', padding: '0',
            fontFamily: '"Noto Sans KR", sans-serif',
            color: UTF8ToString(color), background: UTF8ToString(background)
        });
        // 브라우저가 조합을 끝낸 뒤에만 검증/길이 제한을 적용합니다.
        input.addEventListener('compositionstart', function () { state.composing = true; });
        input.addEventListener('compositionend', function () {
            state.composing = false;
            SRNameInput.send(state, 'OnBrowserInput');
        });
        input.addEventListener('input', function (event) {
            if (!state.composing && !event.isComposing) SRNameInput.send(state, 'OnBrowserInput');
        });
        input.addEventListener('keydown', function (event) {
            event.stopPropagation();
            // 한글 확정 Enter가 닉네임 제출까지 실행되는 것을 방지합니다.
            if (event.isComposing || state.composing || event.keyCode === 229) return;
            if (event.key === 'Enter') {
                event.preventDefault();
                SRNameInput.send(state, 'OnBrowserSubmit');
            } else if (event.key === 'Escape') input.blur();
        });
        input.addEventListener('keyup', function (event) { event.stopPropagation(); });
        input.addEventListener('blur', function () {
            if (SRNameInput.active === state) SRNameInput.send(state, 'OnBrowserBlur');
        });
        // 전체화면에서도 canvas와 같은 fullscreen subtree 안에 있어야 표시됩니다.
        var parent = document.fullscreenElement || document.body;
        if (parent === Module.canvas) {
            // canvas 자체의 fallback 자식은 렌더링되지 않으므로 전체화면에서 나와 입력합니다.
            document.exitFullscreen();
            parent = document.body;
        }
        parent.appendChild(input);
        input.focus();
        input.select();
    },
    SRNameRect__deps: ['$SRNameInput'],
    SRNameRect: function (receiver, x, y, width, height, fontSize) {
        var state = SRNameInput.active;
        if (!state || state.receiver !== UTF8ToString(receiver)) return;
        var rect = Module.canvas.getBoundingClientRect();
        Object.assign(state.input.style, {
            left: (rect.left + x * rect.width) + 'px', top: (rect.top + y * rect.height) + 'px',
            width: (width * rect.width) + 'px', height: (height * rect.height) + 'px',
            fontSize: (fontSize * rect.height) + 'px'
        });
    },
    SRNameSet__deps: ['$SRNameInput'],
    SRNameSet: function (receiver, value) {
        var state = SRNameInput.active;
        if (!state || state.receiver !== UTF8ToString(receiver)) return;
        var caret = state.input.selectionStart;
        state.input.value = UTF8ToString(value);
        state.input.setSelectionRange(caret, caret);
    },
    SRNameClose__deps: ['$SRNameInput'],
    SRNameClose: function (receiver) {
        var state = SRNameInput.active;
        if (state && state.receiver === UTF8ToString(receiver)) SRNameInput.close(state);
    }
});
