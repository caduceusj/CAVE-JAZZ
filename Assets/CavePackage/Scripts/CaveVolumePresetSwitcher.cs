using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CavePackage
{
    /// <summary>
    /// Troca o perfil do Global Volume entre presets para comparar "looks" ao vivo na CAVE.
    ///
    /// Teclas (Play Mode):
    ///   1–9          escolhe o preset (ordem da lista)
    ///   0            imagem crua, sem pós-processamento (liga/desliga)
    ///   PgDn / ]     próximo preset      PgUp / [   preset anterior
    ///   + / -        ajuste fino de exposição (não altera os assets; aparece no rótulo)
    ///   Backspace    zera o ajuste de exposição
    ///   H            mostra/oculta o rótulo
    /// O rótulo é desenhado só no monitor do operador (OnGUI), nunca nas paredes da CAVE.
    /// Em Edit Mode, o preset escolhido no campo "index" é aplicado ao Volume e fica salvo na cena.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("CAVE/Cave Volume Preset Switcher")]
    public class CaveVolumePresetSwitcher : MonoBehaviour
    {
        [Tooltip("Volume global a controlar. Vazio = o Volume deste mesmo objeto.")]
        public Volume volume;

        [Tooltip("Perfis para comparar. Tecla 1 = primeiro da lista, 2 = segundo... (até 9).")]
        public VolumeProfile[] presets = new VolumeProfile[0];

        [Tooltip("Preset ativo. Escolhido aqui em Edit Mode, vira o padrão salvo na cena.")]
        [Min(0)] public int index;

        [Tooltip("Perfil da tecla 0: imagem sem pós-processamento (sem tonemapping, bloom ou grading).")]
        public VolumeProfile rawProfile;

        [Header("Testes na sala (Play Mode)")]
        [Tooltip("Atalhos de teclado.")]
        public bool keyboardShortcuts = true;

        [Tooltip("Passo do ajuste fino de exposição nas teclas + e - (em EV).")]
        [Min(0.05f)] public float exposureStep = 0.25f;

        [Tooltip("Mostra o nome do preset no monitor do operador (não aparece na CAVE).")]
        public bool showLabel = true;

        [Tooltip("Segundos que o rótulo fica na tela depois de uma troca (0 = sempre).")]
        [Min(0f)] public float labelSeconds = 5f;

        [Tooltip("Lembra o último preset escolhido entre execuções (PlayerPrefs).")]
        public bool rememberLastChoice = false;

        const string PrefKey = "CavePackage.VolumePreset";

        bool _raw;
        float _exposureOffset;
        float _labelUntil;
        GameObject _nudgeObject;
        Volume _nudgeVolume;
        VolumeProfile _nudgeProfile;
        ColorAdjustments _nudgeAdjustments;
        GUIStyle _labelStyle;

        /// <summary>Perfil do preset selecionado (ignora o modo cru).</summary>
        public VolumeProfile CurrentPreset =>
            presets != null && index >= 0 && index < presets.Length ? presets[index] : null;

        /// <summary>True enquanto a imagem crua (tecla 0) está ativa.</summary>
        public bool IsRaw => _raw;

        /// <summary>Ajuste fino de exposição aplicado por cima do preset (EV).</summary>
        public float ExposureOffset => _exposureOffset;

        // ================================================================== ciclo de vida
        void OnEnable()
        {
            if (volume == null) volume = GetComponent<Volume>();
            if (Application.isPlaying && rememberLastChoice && presets != null && presets.Length > 0)
                index = Mathf.Clamp(PlayerPrefs.GetInt(PrefKey, index), 0, presets.Length - 1);
            ApplyProfile();
        }

        void OnDisable()
        {
            DestroyNudge();
        }

        void OnValidate()
        {
            if (presets != null && presets.Length > 0) index = Mathf.Clamp(index, 0, presets.Length - 1);
            else index = 0;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // Aplicar fora do OnValidate (Unity não gosta de mudanças em outros objetos durante a validação).
                EditorApplication.delayCall -= ApplyFromEditor;
                EditorApplication.delayCall += ApplyFromEditor;
                return;
            }
#endif
            if (volume == null) volume = GetComponent<Volume>();
            ApplyProfile();
            UpdateExposureNudge();
        }

#if UNITY_EDITOR
        void ApplyFromEditor()
        {
            if (this == null) return; // objeto destruído antes do delayCall
            if (volume == null) volume = GetComponent<Volume>();
            ApplyProfile();
        }
#endif

        void Update()
        {
            if (Application.isPlaying && keyboardShortcuts) HandleKeys();
        }

        // ================================================================== API pública
        /// <summary>Seleciona o preset i (0 = primeiro da lista).</summary>
        public void Select(int i)
        {
            if (presets == null || presets.Length == 0) return;
            index = Mathf.Clamp(i, 0, presets.Length - 1);
            _raw = false;
            ApplyProfile();
            UpdateExposureNudge();
            if (rememberLastChoice)
            {
                PlayerPrefs.SetInt(PrefKey, index);
                PlayerPrefs.Save();
            }
            Announce();
        }

        /// <summary>Avança (+1) ou volta (-1) na lista, dando a volta no fim.</summary>
        public void Step(int delta)
        {
            if (presets == null || presets.Length == 0) return;
            int n = presets.Length;
            Select(((index + delta) % n + n) % n);
        }

        /// <summary>Liga/desliga a imagem crua (perfil rawProfile).</summary>
        public void ToggleRaw()
        {
            if (rawProfile == null)
            {
                Debug.LogWarning("[CAVE] Nenhum perfil 'cru' atribuído ao CaveVolumePresetSwitcher.", this);
                return;
            }
            _raw = !_raw;
            ApplyProfile();
            UpdateExposureNudge();
            Announce();
        }

        /// <summary>Soma 'delta' EV ao ajuste fino de exposição (não altera os assets).</summary>
        public void NudgeExposure(float delta)
        {
            _exposureOffset = Mathf.Round((_exposureOffset + delta) * 100f) / 100f;
            UpdateExposureNudge();
            Announce();
        }

        public void ResetExposure()
        {
            _exposureOffset = 0f;
            UpdateExposureNudge();
            Announce();
        }

        // ================================================================== aplicação
        void ApplyProfile()
        {
            if (volume == null) return;
            VolumeProfile target = _raw ? rawProfile : CurrentPreset;
            if (target == null || volume.sharedProfile == target) return;
            volume.sharedProfile = target;
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(volume);
#endif
        }

        float CurrentProfileExposure()
        {
            var profile = _raw ? rawProfile : CurrentPreset;
            if (profile != null && profile.TryGet(out ColorAdjustments adjustments) &&
                adjustments.active && adjustments.postExposure.overrideState)
                return adjustments.postExposure.value;
            return 0f;
        }

        // O ajuste fino usa um Volume global extra, criado só em runtime, com prioridade maior que o
        // principal e apenas o Post Exposure sobrescrito (preset + ajuste). Os assets não são tocados.
        void UpdateExposureNudge()
        {
            bool needed = Application.isPlaying && volume != null && Mathf.Abs(_exposureOffset) > 1e-4f;
            if (!needed)
            {
                if (_nudgeVolume != null) _nudgeVolume.enabled = false;
                return;
            }

            if (_nudgeVolume == null)
            {
                _nudgeObject = new GameObject("CAVE Exposure Nudge (runtime)") { hideFlags = HideFlags.DontSave };
                _nudgeObject.layer = volume.gameObject.layer;
                _nudgeObject.transform.SetParent(transform, false);
                _nudgeVolume = _nudgeObject.AddComponent<Volume>();
                _nudgeVolume.isGlobal = true;
                _nudgeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                _nudgeProfile.name = "CAVE Exposure Nudge";
                _nudgeProfile.hideFlags = HideFlags.DontSave;
                _nudgeAdjustments = _nudgeProfile.Add<ColorAdjustments>(false);
                _nudgeVolume.sharedProfile = _nudgeProfile;
            }

            _nudgeVolume.priority = volume.priority + 1f;
            _nudgeVolume.weight = 1f;
            _nudgeVolume.enabled = true;
            _nudgeAdjustments.postExposure.Override(CurrentProfileExposure() + _exposureOffset);
        }

        void DestroyNudge()
        {
            if (_nudgeObject != null)
            {
                if (Application.isPlaying) Destroy(_nudgeObject);
                else DestroyImmediate(_nudgeObject);
            }
            if (_nudgeProfile != null)
            {
                if (Application.isPlaying) Destroy(_nudgeProfile);
                else DestroyImmediate(_nudgeProfile);
            }
            _nudgeObject = null;
            _nudgeVolume = null;
            _nudgeProfile = null;
            _nudgeAdjustments = null;
        }

        // ================================================================== teclado
        void HandleKeys()
        {
            int count = presets != null ? Mathf.Min(presets.Length, 9) : 0;
            for (int i = 0; i < count; i++)
            {
                if (DigitPressed(i + 1))
                {
                    Select(i);
                    return;
                }
            }
            if (DigitPressed(0)) ToggleRaw();
            else if (Pressed(KeyAction.Next)) Step(+1);
            else if (Pressed(KeyAction.Previous)) Step(-1);
            else if (Pressed(KeyAction.ExposureUp)) NudgeExposure(+exposureStep);
            else if (Pressed(KeyAction.ExposureDown)) NudgeExposure(-exposureStep);
            else if (Pressed(KeyAction.ExposureReset)) ResetExposure();
            else if (Pressed(KeyAction.ToggleLabel))
            {
                showLabel = !showLabel;
                Announce();
            }
        }

        enum KeyAction { Next, Previous, ExposureUp, ExposureDown, ExposureReset, ToggleLabel }

#if ENABLE_INPUT_SYSTEM
        static readonly Key[] s_DigitKeys =
            { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
        static readonly Key[] s_NumpadKeys =
            { Key.Numpad0, Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9 };

        static bool KeyDown(Key key)
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard[key].wasPressedThisFrame;
        }

        static bool DigitPressed(int digit) => KeyDown(s_DigitKeys[digit]) || KeyDown(s_NumpadKeys[digit]);

        static bool Pressed(KeyAction action)
        {
            switch (action)
            {
                case KeyAction.Next: return KeyDown(Key.PageDown) || KeyDown(Key.RightBracket);
                case KeyAction.Previous: return KeyDown(Key.PageUp) || KeyDown(Key.LeftBracket);
                case KeyAction.ExposureUp: return KeyDown(Key.Equals) || KeyDown(Key.NumpadPlus);
                case KeyAction.ExposureDown: return KeyDown(Key.Minus) || KeyDown(Key.NumpadMinus);
                case KeyAction.ExposureReset: return KeyDown(Key.Backspace);
                default: return KeyDown(Key.H);
            }
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        static readonly KeyCode[] s_DigitKeys =
            { KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9 };
        static readonly KeyCode[] s_NumpadKeys =
            { KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9 };

        static bool DigitPressed(int digit) => Input.GetKeyDown(s_DigitKeys[digit]) || Input.GetKeyDown(s_NumpadKeys[digit]);

        static bool Pressed(KeyAction action)
        {
            switch (action)
            {
                case KeyAction.Next: return Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.RightBracket);
                case KeyAction.Previous: return Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.LeftBracket);
                case KeyAction.ExposureUp: return Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
                case KeyAction.ExposureDown: return Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
                case KeyAction.ExposureReset: return Input.GetKeyDown(KeyCode.Backspace);
                default: return Input.GetKeyDown(KeyCode.H);
            }
        }
#else
        static bool DigitPressed(int digit) => false;
        static bool Pressed(KeyAction action) => false;
#endif

        // ================================================================== rótulo do operador
        void Announce()
        {
            _labelUntil = Time.unscaledTime + labelSeconds;
            Debug.Log($"[CAVE] Look: {DescribeCurrent()}", this);
        }

        string DescribeCurrent()
        {
            string look = _raw
                ? "0 · cru (sem pós-processamento)"
                : $"{index + 1} · {(CurrentPreset != null ? CurrentPreset.name : "(vazio)")}";
            if (Mathf.Abs(_exposureOffset) > 1e-4f)
                look += $"   |   exposição {_exposureOffset:+0.00;-0.00} EV";
            return look;
        }

        void OnGUI()
        {
            if (!Application.isPlaying || !showLabel) return;
            if (labelSeconds > 0f && Time.unscaledTime > _labelUntil) return;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 18,
                    alignment = TextAnchor.UpperLeft,
                    richText = true,
                    wordWrap = false,
                    padding = new RectOffset(14, 14, 10, 10),
                };
            }

            int count = presets != null ? Mathf.Min(presets.Length, 9) : 0;
            string text = $"<b>CAVE  ·  {DescribeCurrent()}</b>\n" +
                          $"1–{count} presets   0 cru   PgUp/PgDn   +/− exposição   Backspace zera   H oculta";
            var content = new GUIContent(text);
            Vector2 size = _labelStyle.CalcSize(content);
            GUI.Box(new Rect(16f, 16f, size.x, size.y), content, _labelStyle);
        }
    }
}
