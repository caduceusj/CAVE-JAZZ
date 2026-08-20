using UnityEngine;

namespace CaveJazz.Calibration
{
    /// <summary>Como o gemeo se posiciona em relacao ao rig que ele espelha.</summary>
    public enum TwinPlacement
    {
        /// <summary>O transform e seu; o builder nao encosta nele.</summary>
        Livre = 0,

        /// <summary>Ocupa exatamente o lugar da CAVE real.</summary>
        SobreORig = 1,

        /// <summary>Maquete ao lado da cena, deslocada por <see cref="CaveTwin.offsetFromSource"/>.</summary>
        DeslocadoDoRig = 2
    }

    /// <summary>
    /// Gemeo digital da CAVE: uma replica real da sala imersiva, com as cinco telas
    /// mostrando as Render Textures ao vivo, o casco fisico em volta e uma referencia
    /// de escala humana. Ao contrario dos gizmos do <see cref="CavePreview"/>, o gemeo
    /// e feito de GameObjects normais, salvos na cena, que podem ser iluminados,
    /// ajustados a mao e virar prefab.
    ///
    /// Este componente guarda so a configuracao e o posicionamento. A geracao vive no
    /// editor (CaveTwinBuilder), porque criar material como asset e uma operacao de editor.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("CAVE/Cave Twin")]
    public class CaveTwin : MonoBehaviour
    {
        public const string ScreensGroup = "Telas";
        public const string StructureGroup = "Estrutura";
        public const string ReferencesGroup = "Referencias";

        public const string EyeMarkerName = "PontoDeOlho";
        public const string HumanReferenceName = "Referencia_Humana";
        public const string ObserverName = "ObservadorInterno";

        [Header("Origem")]
        [Tooltip("Rig que o gemeo espelha. Vazio = procura um CaveRig na cena.")]
        public CaveRig source;

        [Header("Posicionamento")]
        public TwinPlacement placement = TwinPlacement.DeslocadoDoRig;

        [Tooltip("Deslocamento em espaco de mundo, usado no modo 'Deslocado do rig'. " +
                 "O padrao tira o gemeo de cima da cena do bar, que ocupa cerca de 20 unidades.")]
        public Vector3 offsetFromSource = new Vector3(50f, 0f, 0f);

        [Tooltip("Copia a rotacao do rig, para o gemeo ficar orientado como a CAVE real.")]
        public bool matchSourceRotation = true;

        [Header("Estrutura")]
        [Tooltip("Espessura do casco: piso e as quatro paredes atras das telas.")]
        [Min(0.01f)]
        public float structureThickness = 0.3f;

        [Tooltip("Espessura dos pilares de canto e das vigas que contornam o topo aberto.")]
        [Min(0.01f)]
        public float edgeThickness = 0.15f;

        [Tooltip("Mantem os colliders que vem com as primitivas. Desligado por padrao: " +
                 "o gemeo e para olhar, nao para colidir.")]
        public bool generateColliders;

        [Header("Referencia de escala")]
        [Tooltip("Quantos metros vale 1 unidade Unity. Afeta so a figura de referencia; " +
                 "a geometria da sala vem das Render Textures.")]
        [Min(0.0001f)]
        public float metersPerUnit = 1f;

        [Tooltip("Altura da figura de referencia, em metros.")]
        [Min(0.1f)]
        public float humanHeightMeters = 1.7f;

        [Tooltip("Mesh da figura de referencia. Vazio = capsula. Aceita a mesh de " +
                 "SM_NPC.fbx, que e reescalada pelos bounds para bater a altura.")]
        public Mesh humanReferenceMesh;

        [Header("Camada")]
        [Tooltip("Camada dedicada do gemeo. Sem ela, as cameras da CAVE (far clip 1000) " +
                 "enxergariam a maquete e ela apareceria dentro do proprio render.")]
        public string twinLayerName = "CaveTwin";

        [Tooltip("Remove a camada do gemeo do culling mask de todas as cameras do rig.")]
        public bool excludeFromRigCameras = true;

        /// <summary>Altura da figura de referencia em unidades Unity.</summary>
        public float HumanHeightInUnits => humanHeightMeters / Mathf.Max(0.0001f, metersPerUnit);

        private void OnEnable()
        {
            ApplyPlacement();
        }

        private void Update()
        {
            // O gemeo acompanha o rig se ele for movido. No modo Livre nada acontece,
            // e arrastar a maquete pela cena funciona normalmente.
            ApplyPlacement();
        }

        /// <summary>
        /// Procura o rig no campo e, se estiver vazio, na cena. Comparacoes explicitas
        /// com null porque UnityEngine.Object tem null "falso".
        /// </summary>
        public CaveRig ResolveSource()
        {
            if (source != null)
            {
                return source;
            }

#if UNITY_2022_2_OR_NEWER
            return FindFirstObjectByType<CaveRig>(FindObjectsInactive.Include);
#else
            return FindObjectOfType<CaveRig>(true);
#endif
        }

        /// <summary>Recoloca o gemeo em relacao ao rig, conforme o modo escolhido.</summary>
        public void ApplyPlacement()
        {
            if (placement == TwinPlacement.Livre)
            {
                return;
            }

            CaveRig rig = ResolveSource();
            if (rig == null)
            {
                return;
            }

            Transform rigTransform = rig.transform;

            Vector3 position = placement == TwinPlacement.DeslocadoDoRig
                ? rigTransform.position + offsetFromSource
                : rigTransform.position;

            // So escreve quando muda de fato: escrever igual marcaria a cena como suja
            // a cada tick do editor.
            if (transform.position != position)
            {
                transform.position = position;
            }

            if (matchSourceRotation && transform.rotation != rigTransform.rotation)
            {
                transform.rotation = rigTransform.rotation;
            }
        }
    }
}
