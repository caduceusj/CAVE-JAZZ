using UnityEngine;

namespace CaveJazz.Calibration
{
    /// <summary>
    /// Camada de diagnostico da CAVE, desenhada por cima do CaveRoot no Scene View:
    /// a caixa da sala, o ponto de olho, o frustum de cada camera e a area que ela
    /// realmente alcanca na parede, em vermelho quando foge da tolerancia do rig.
    ///
    /// A replica visual da sala e trabalho do <see cref="CaveTwin"/>, que gera geometria
    /// de verdade com as Render Textures. Aqui nao se cria objeto nenhum: sao so gizmos,
    /// para o diagnostico poder ficar ligado sem sujar a cena.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CaveRig))]
    [AddComponentMenu("CAVE/Cave Preview")]
    public class CavePreview : MonoBehaviour
    {
        [Header("Gizmos")]
        public bool showWireBox = true;
        public bool showFaceOutlines = true;
        public bool showEyePoint = true;

        [Tooltip("Liga o ponto de olho aos quatro cantos que cada camera realmente alcanca na parede.")]
        public bool showFrustums = true;

        [Tooltip("Pinta de vermelho a area realmente coberta quando ela foge da tolerancia do rig.")]
        public bool highlightMismatch = true;

        [Tooltip("Desenha os gizmos mesmo com o CaveRoot fora da selecao.")]
        public bool alwaysDrawGizmos = true;

        [Header("Cores")]
        public Color boxColor = new Color(0.25f, 0.85f, 1f, 1f);
        public Color faceColor = new Color(0.25f, 0.85f, 1f, 0.6f);
        public Color eyeColor = new Color(1f, 0.85f, 0.2f, 1f);
        public Color coverageOkColor = new Color(0.3f, 1f, 0.45f, 1f);
        public Color coverageErrorColor = new Color(1f, 0.3f, 0.3f, 1f);

        private CaveRig cachedRig;

        public CaveRig Rig
        {
            get
            {
                if (cachedRig == null)
                {
                    cachedRig = GetComponent<CaveRig>();
                }

                return cachedRig;
            }
        }

        private void OnDrawGizmos()
        {
            if (alwaysDrawGizmos)
            {
                DrawGizmos();
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!alwaysDrawGizmos)
            {
                DrawGizmos();
            }
        }

        private void DrawGizmos()
        {
            CaveRig rig = Rig;
            if (rig == null)
            {
                return;
            }

            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;

            Gizmos.matrix = transform.localToWorldMatrix;

            if (showWireBox)
            {
                Gizmos.color = boxColor;
                Gizmos.DrawWireCube(rig.CenterLocal, rig.Dimensions);
            }

            if (showFaceOutlines)
            {
                Gizmos.color = faceColor;
                for (int i = 0; i < CaveRig.FaceCount; i++)
                {
                    DrawRectGizmo(rig.GetFaceRect((CaveFace)i));
                }
            }

            if (showEyePoint)
            {
                Gizmos.color = eyeColor;
                Vector3 eye = rig.EyeLocalPosition;
                float size = Mathf.Max(0.1f, rig.Dimensions.magnitude * 0.01f);

                Gizmos.DrawLine(eye - Vector3.right * size, eye + Vector3.right * size);
                Gizmos.DrawLine(eye - Vector3.up * size, eye + Vector3.up * size);
                Gizmos.DrawLine(eye - Vector3.forward * size, eye + Vector3.forward * size);
                Gizmos.DrawWireSphere(eye, size * 0.5f);
            }

            Gizmos.matrix = previousMatrix;

            if (showFrustums || highlightMismatch)
            {
                DrawCoverageGizmos(rig);
            }

            Gizmos.color = previousColor;
        }

        private static void DrawRectGizmo(CaveFaceRect rect)
        {
            Vector3 c0 = rect.Corner(0);
            Vector3 c1 = rect.Corner(1);
            Vector3 c2 = rect.Corner(2);
            Vector3 c3 = rect.Corner(3);

            Gizmos.DrawLine(c0, c1);
            Gizmos.DrawLine(c1, c3);
            Gizmos.DrawLine(c3, c2);
            Gizmos.DrawLine(c2, c0);
        }

        /// <summary>
        /// Desenha, em espaco de mundo, o retangulo que cada camera realmente alcanca na
        /// parede e as arestas do frustum ate ele. E a leitura visual direta da calibracao:
        /// verde encostado na borda da face significa cobertura correta.
        /// </summary>
        private void DrawCoverageGizmos(CaveRig rig)
        {
            Vector3[] hits = new Vector3[4];

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                Camera camera = rig.GetCamera(face);
                if (camera == null)
                {
                    continue;
                }

                CaveFaceRect rect = rig.GetFaceRect(face);
                Vector3 planePoint = transform.TransformPoint(rect.center);
                Plane plane = new Plane(transform.TransformDirection(rect.normal), planePoint);

                bool reachable = true;
                for (int corner = 0; corner < 4; corner++)
                {
                    float u = (corner & 1) == 0 ? 0f : 1f;
                    float v = (corner & 2) == 0 ? 0f : 1f;

                    Ray ray = camera.ViewportPointToRay(new Vector3(u, v, 0f));
                    if (!plane.Raycast(ray, out float enter) || enter <= 0f)
                    {
                        reachable = false;
                        break;
                    }

                    hits[corner] = ray.GetPoint(enter);
                }

                if (!reachable)
                {
                    continue;
                }

                CaveCoverage coverage = rig.GetCoverage(face, camera);
                bool withinTolerance = coverage.valid && coverage.WorstPercent <= rig.mismatchTolerancePercent;

                Gizmos.color = !highlightMismatch || withinTolerance ? coverageOkColor : coverageErrorColor;

                Gizmos.DrawLine(hits[0], hits[1]);
                Gizmos.DrawLine(hits[1], hits[3]);
                Gizmos.DrawLine(hits[3], hits[2]);
                Gizmos.DrawLine(hits[2], hits[0]);

                if (showFrustums)
                {
                    Vector3 origin = camera.transform.position;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        Gizmos.DrawLine(origin, hits[corner]);
                    }
                }
            }
        }
    }
}
