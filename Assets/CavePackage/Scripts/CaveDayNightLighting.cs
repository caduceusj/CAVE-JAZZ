using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace CavePackage
{
    /// <summary>
    /// Iluminação de dia e de noite "estilo Hollywood" (day-for-night).
    ///
    /// No cinema a noite não é escura: ela é AZULADA, com uma luz principal ("lua") ainda forte,
    /// contraluz e um preenchimento ambiente suficiente para ver rostos e objetos. O que muda entre
    /// dia e noite é principalmente a cor e a direção da luz, não a quantidade.
    /// Este componente interpola entre um preset de dia e um de noite sem deixar a cena "apagar":
    /// a luz principal vira lua em vez de desligar e o ambiente nunca vai a preto.
    ///
    /// Em CAVE de projeção isso é ainda mais importante: o preto de uma parede é lavado pela luz
    /// refletida das outras paredes, então cenas escuras viram cinza-lamacento. Mantenha a imagem
    /// nos tons médios e use cor (azul/ciano) para dizer "noite".
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("CAVE/Cave Day Night Lighting")]
    public class CaveDayNightLighting : MonoBehaviour
    {
        [Tooltip("1 = dia, 0 = noite. Valores intermediários = amanhecer / entardecer.")]
        [Range(0f, 1f)] public float dayAmount = 1f;

        [Tooltip("Faz o ciclo dia → noite → dia sozinho durante o Play Mode.")]
        public bool autoCycle = false;

        [Tooltip("Duração de um ciclo completo (s).")]
        [Min(1f)] public float cycleDurationSeconds = 300f;

        [Tooltip("Luz direcional que faz o papel de sol (dia) e de lua (noite).")]
        public Light keyLight;

        [Tooltip("Multiplicador geral de luz ('quanta luz entra no olho do público'). Suba antes de mexer em cada luz.")]
        [Min(0f)] public float brightness = 1f;

        public CaveLightingPreset day = CaveLightingPreset.DefaultDay;
        public CaveLightingPreset night = CaveLightingPreset.DefaultNight;

        [Tooltip("Controla a luz ambiente da cena (Environment Lighting = Gradient).")]
        public bool controlAmbient = true;

        [Tooltip("Controla a neblina da cena.")]
        public bool controlFog = false;

        [Tooltip("Opcional: Volume global com o pós-processamento do DIA (o peso acompanha dayAmount).")]
        public Volume dayVolume;

        [Tooltip("Opcional: Volume global com o pós-processamento da NOITE (ex.: White Balance mais frio, Post Exposure +0.5).")]
        public Volume nightVolume;

        void OnEnable() => Apply();

        void Update()
        {
            if (autoCycle && Application.isPlaying && cycleDurationSeconds > 0f)
                dayAmount = 0.5f + 0.5f * Mathf.Cos(Time.time * 2f * Mathf.PI / cycleDurationSeconds);
            Apply();
        }

        void OnValidate() => Apply();

        /// <summary>Aplica o preset interpolado na luz principal, no ambiente, na neblina e nos volumes.</summary>
        public void Apply()
        {
            var p = CaveLightingPreset.Lerp(night, day, dayAmount);

            if (keyLight != null)
            {
                keyLight.color = p.keyColor;
                keyLight.intensity = p.keyIntensity * brightness;
                keyLight.transform.rotation = Quaternion.Euler(p.keyElevation, p.keyAzimuth, 0f);
            }

            if (controlAmbient)
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = p.ambientSky * brightness;
                RenderSettings.ambientEquatorColor = p.ambientEquator * brightness;
                RenderSettings.ambientGroundColor = p.ambientGround * brightness;
            }

            if (controlFog)
            {
                RenderSettings.fog = p.fogDensity > 0f;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = p.fogColor;
                RenderSettings.fogDensity = p.fogDensity;
            }

            if (dayVolume != null) dayVolume.weight = dayAmount;
            if (nightVolume != null) nightVolume.weight = 1f - dayAmount;
        }
    }

    [Serializable]
    public struct CaveLightingPreset
    {
        [Header("Luz principal (sol / lua)")]
        public Color keyColor;
        [Min(0f)] public float keyIntensity;
        [Tooltip("Graus acima do horizonte.")]
        [Range(-10f, 90f)] public float keyElevation;
        [Tooltip("Direção horizontal (graus em torno de Y).")]
        [Range(-180f, 180f)] public float keyAzimuth;

        [Header("Ambiente (gradiente céu / horizonte / chão)")]
        public Color ambientSky;
        public Color ambientEquator;
        public Color ambientGround;

        [Header("Neblina")]
        public Color fogColor;
        [Min(0f)] public float fogDensity;

        /// <summary>Dia: sol quente e alto, ambiente claro.</summary>
        public static CaveLightingPreset DefaultDay => new CaveLightingPreset
        {
            keyColor = new Color(1f, 0.95f, 0.86f),
            keyIntensity = 1.6f,
            keyElevation = 55f,
            keyAzimuth = 30f,
            ambientSky = new Color(0.62f, 0.74f, 0.92f),
            ambientEquator = new Color(0.50f, 0.54f, 0.58f),
            ambientGround = new Color(0.30f, 0.27f, 0.24f),
            fogColor = new Color(0.62f, 0.72f, 0.82f),
            fogDensity = 0.004f,
        };

        /// <summary>
        /// Noite "de cinema": lua azul-clara ainda forte (≈ metade do sol), ambiente azul que nunca
        /// chega a preto. Ajuste a cor, não a quantidade de luz.
        /// </summary>
        public static CaveLightingPreset DefaultNight => new CaveLightingPreset
        {
            keyColor = new Color(0.62f, 0.74f, 1f),
            keyIntensity = 0.8f,
            keyElevation = 35f,
            keyAzimuth = -40f,
            ambientSky = new Color(0.22f, 0.32f, 0.55f),
            ambientEquator = new Color(0.15f, 0.21f, 0.34f),
            ambientGround = new Color(0.08f, 0.09f, 0.13f),
            fogColor = new Color(0.10f, 0.16f, 0.28f),
            fogDensity = 0.006f,
        };

        public static CaveLightingPreset Lerp(CaveLightingPreset a, CaveLightingPreset b, float t)
        {
            t = Mathf.Clamp01(t);
            return new CaveLightingPreset
            {
                keyColor = Color.Lerp(a.keyColor, b.keyColor, t),
                keyIntensity = Mathf.Lerp(a.keyIntensity, b.keyIntensity, t),
                keyElevation = Mathf.Lerp(a.keyElevation, b.keyElevation, t),
                keyAzimuth = Mathf.LerpAngle(a.keyAzimuth, b.keyAzimuth, t),
                ambientSky = Color.Lerp(a.ambientSky, b.ambientSky, t),
                ambientEquator = Color.Lerp(a.ambientEquator, b.ambientEquator, t),
                ambientGround = Color.Lerp(a.ambientGround, b.ambientGround, t),
                fogColor = Color.Lerp(a.fogColor, b.fogColor, t),
                fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t),
            };
        }
    }
}
