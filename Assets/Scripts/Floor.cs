using UnityEngine;

namespace OneTapDemolition
{
    public enum AimState
    {
        None,
        InChain,
        Target
    }

    /// <summary>
    /// 1階分のロジック。タップされると物理演算を有効化して弾け飛ぶ。
    /// 種類(通常/ゲート/保護)に応じた色分けと看板、狙っている間のハイライトも担当する。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class Floor : MonoBehaviour
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        // 階を横に広げたので、看板が階の面にめり込まないよう十分手前に浮かせる
        private const float MinBadgeDistance = 2.3f;
        private static readonly Color FallbackDebrisTint = new Color(0.6f, 0.57f, 0.53f, 1f);
        private static readonly Color GateX2Color = new Color(0.55f, 1f, 0.6f, 1f);
        private static readonly Color GateX3Color = new Color(1f, 0.85f, 0.25f, 1f);
        private static readonly Color GateBadColor = new Color(1f, 0.45f, 0.45f, 1f);
        private static readonly Color GateNeutralColor = new Color(0.72f, 0.8f, 0.92f, 1f);
        // 倍率が変わる何秒前から予告の光を出すか
        private const float GateWarningSeconds = 0.3f;
        private static readonly Color BonusColor = new Color(1f, 0.82f, 0.2f, 1f);

        [Header("Physics")]
        [SerializeField] private float launchForce = 3.5f;
        [SerializeField] private float launchTorque = 2.5f;
        [Tooltip("hitPointから見た方向にどれだけ従うか(0=完全ランダム散らばり、1=常に同じ方向)。")]
        [SerializeField] private float hitDirectionWeight = 0.4f;
        [SerializeField] private float scatterWeight = 0.8f;
        [SerializeField] private float upwardLift = 0.6f;
        [Tooltip("連鎖が進むほど吹っ飛びが強くなる割合(段ごとの加算率)。")]
        [SerializeField] private float chainKickPerStep = 0.06f;
        [Tooltip("ゲート階・保護階を壊したときの追加の吹っ飛び倍率(壊した実感を強める)。")]
        [SerializeField] private float specialKick = 1.5f;

        [Header("Visuals")]
        [SerializeField] private GameObject accentBand;
        [SerializeField] private int accentInterval = 4;
        [SerializeField] private float badgeDistance = 1.6f;

        private Rigidbody rb;
        private MeshRenderer bodyRenderer;
        private MaterialPropertyBlock block;
        private Color bodyBaseColor = Color.white;
        private BuildingTower ownerTower;
        private FloorBadge badge;
        private int floorIndex;
        private bool isDemolished;
        private Color debrisTint = FallbackDebrisTint;
        private Color kindTint = Color.white;
        private AimState aimState = AimState.None;
        private bool cracking;
        private Vector3 restPosition;
        private bool gateLocked;
        private float shownGateValue = -1f;

        public FloorSpec Spec { get; private set; } = FloorSpec.Normal;
        public int FloorIndex => floorIndex;
        public bool IsDemolished => isDemolished;
        public BuildingTower OwnerTower => ownerTower;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.isKinematic = true;
            bodyRenderer = GetComponent<MeshRenderer>();
            block = new MaterialPropertyBlock();
            if (bodyRenderer != null && bodyRenderer.sharedMaterial != null && bodyRenderer.sharedMaterial.HasProperty(ColorId))
            {
                bodyBaseColor = bodyRenderer.sharedMaterial.GetColor(ColorId);
            }
            CacheDebrisTint();
        }

        /// <summary>
        /// 建物バリアントのAccentマテリアルの色を1回だけ拾い、通常階の崩落デブリの色にする。
        /// </summary>
        private void CacheDebrisTint()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                Material material = renderer.sharedMaterial;
                if (material == null || material.name.IndexOf("Accent", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Color baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor")
                    : material.HasProperty("_Color") ? material.color
                    : FallbackDebrisTint;
                debrisTint = Color.Lerp(baseColor, FallbackDebrisTint, 0.35f);
                return;
            }
        }

        /// <summary>
        /// タワー生成時にBuildingTowerから呼ばれる初期化。
        /// </summary>
        public void Setup(int index, BuildingTower tower, FloorSpec spec)
        {
            floorIndex = index;
            ownerTower = tower;
            isDemolished = false;
            gateLocked = false;
            shownGateValue = -1f;
            rb.isKinematic = true;

            if (accentBand != null)
            {
                accentBand.SetActive(accentInterval > 0 && index % accentInterval == 0);
            }

            ApplyKind(spec);
        }

        private void ApplyKind(FloorSpec spec)
        {
            Spec = spec;
            string label = null;
            switch (spec.Kind)
            {
                case FloorKind.Gate:
                    shownGateValue = spec.ValueAt(GateClock.Now);
                    kindTint = TintForGate(shownGateValue);
                    label = LabelForGate(shownGateValue);
                    break;
                case FloorKind.Bonus:
                    kindTint = BonusColor;
                    break;
                default:
                    kindTint = Color.white;
                    break;
            }

            if (spec.Kind != FloorKind.Normal)
            {
                debrisTint = Color.Lerp(kindTint, FallbackDebrisTint, 0.2f);
                badge = spec.Kind == FloorKind.Bonus
                    ? FloorBadge.CreateStar(transform, kindTint, Mathf.Max(badgeDistance, MinBadgeDistance))
                    : FloorBadge.Create(transform, label, kindTint, Mathf.Max(badgeDistance, MinBadgeDistance));
            }

            SetAim(AimState.None);
        }

        private static string LabelForGate(float value)
        {
            if (value >= 3f)
            {
                return "×3";
            }
            if (value >= 2f)
            {
                return "×2";
            }
            return value >= 1f ? "×1" : "÷2";
        }

        private static Color TintForGate(float value)
        {
            if (value >= 3f)
            {
                return GateX3Color;
            }
            if (value >= 2f)
            {
                return GateX2Color;
            }
            return value >= 1f ? GateNeutralColor : GateBadColor;
        }

        /// <summary>ライブゲートの現在の倍率を看板と階の色に反映する。</summary>
        private void ShowGateValue(float value)
        {
            shownGateValue = value;
            kindTint = TintForGate(value);
            debrisTint = Color.Lerp(kindTint, FallbackDebrisTint, 0.2f);
            if (badge != null)
            {
                badge.SetLabel(LabelForGate(value), kindTint);
                badge.Pop();
            }
            ApplyTint(1f);
        }

        private void RefreshLiveGate()
        {
            float t = GateClock.Now;
            float value = Spec.ValueAt(t);
            if (!Mathf.Approximately(value, shownGateValue))
            {
                ShowGateValue(value);
            }

            if (badge != null)
            {
                float remaining = Spec.TimeToChange(t);
                badge.SetWarning(remaining < GateWarningSeconds ? 1f - remaining / GateWarningSeconds : 0f);
            }
        }

        /// <summary>
        /// タップが確定した時刻の倍率に固定する。連鎖が崩れる間に看板が切り替わって、掛かる倍率と食い違わないようにする。
        /// </summary>
        public void LockGate(float t)
        {
            if (!Spec.IsLive || gateLocked)
            {
                return;
            }
            gateLocked = true;
            float value = Spec.ValueAt(t);
            if (!Mathf.Approximately(value, shownGateValue))
            {
                ShowGateValue(value);
            }
            if (badge != null)
            {
                badge.SetWarning(0f);
                badge.Pop();
            }
        }

        /// <summary>
        /// 狙っている間のハイライト。Targetはタップ位置、InChainは巻き込まれる階。
        /// </summary>
        public void SetAim(AimState state)
        {
            aimState = state;
            ApplyTint(1f);
        }

        /// <summary>
        /// ボーナス階を金色に脈動させて「特別な階」だと気付かせる。他の階は何もしない。
        /// </summary>
        private void Update()
        {
            if (isDemolished)
            {
                return;
            }
            if (Spec.IsLive && !gateLocked)
            {
                RefreshLiveGate();
            }
            if (Spec.Kind != FloorKind.Bonus || aimState != AimState.None || cracking)
            {
                return;
            }

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4f);
            ApplyTint(Mathf.Lerp(0.75f, 1.25f, pulse));
        }

        /// <summary>
        /// 崩れる直前に階を細かく揺らして白く光らせる(ヒビ入り演出)。duration後にDemolishを呼ぶのは呼び出し側。
        /// </summary>
        public void StartCrack(float duration)
        {
            if (isDemolished || cracking)
            {
                return;
            }
            StartCoroutine(CrackRoutine(duration));
        }

        private System.Collections.IEnumerator CrackRoutine(float duration)
        {
            cracking = true;
            restPosition = transform.localPosition;
            JuiceManager.Instance?.PlayCrackEffect(transform.position);

            float elapsed = 0f;
            while (elapsed < duration && !isDemolished)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                Vector3 jitter = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)) * (0.04f + 0.05f * t);
                transform.localPosition = restPosition + jitter;
                ApplyTint(1f, Mathf.Lerp(0.15f, 0.7f, t));
                yield return null;
            }

            if (!isDemolished)
            {
                transform.localPosition = restPosition;
            }
            cracking = false;
        }

        private void ApplyTint(float brightness, float whiteFlash = 0f)
        {
            if (bodyRenderer == null)
            {
                return;
            }

            AimState state = aimState;
            Color tint = kindTint;
            switch (state)
            {
                case AimState.Target:
                    tint = Color.Lerp(kindTint, new Color(1f, 0.95f, 0.5f, 1f), 0.6f);
                    break;
                case AimState.InChain:
                    tint = Color.Lerp(kindTint, Color.white, 0.3f);
                    break;
            }

            tint = Color.Lerp(tint * brightness, Color.white, whiteFlash);
            block.SetColor(ColorId, new Color(bodyBaseColor.r * tint.r, bodyBaseColor.g * tint.g, bodyBaseColor.b * tint.b, bodyBaseColor.a));
            bodyRenderer.SetPropertyBlock(block);
        }

        /// <summary>
        /// この階を破壊する。物理演算をオンにして弾け飛ばし、演出フックを呼ぶ。
        /// chainStep: この階が連鎖の何番目に崩れたか(1始まり)。
        /// </summary>
        public void Demolish(Vector3 hitPoint, int chainStep)
        {
            if (isDemolished)
            {
                return;
            }

            isDemolished = true;
            rb.isKinematic = false;
            cracking = false;
            SetAim(AimState.None);
            if (badge != null)
            {
                badge.Detach();
                badge = null;
            }

            Vector3 hitDirection = transform.position - hitPoint;
            hitDirection = hitDirection.sqrMagnitude < 0.01f ? Random.onUnitSphere : hitDirection.normalized;

            Vector3 pushDirection = (hitDirection * hitDirectionWeight
                + Random.insideUnitSphere * scatterWeight
                + Vector3.up * upwardLift).normalized;

            float chainKick = 1f + chainKickPerStep * Mathf.Max(0, chainStep - 1);
            float special = Spec.Kind == FloorKind.Normal ? 1f : specialKick;
            float totalForce = launchForce * chainKick * special;

            rb.AddForce(pushDirection * totalForce, ForceMode.Impulse);
            rb.AddTorque(Random.insideUnitSphere * (launchTorque * chainKick * special), ForceMode.Impulse);

            JuiceManager.Instance?.PlayFloorDestroyEffect(transform.position, chainStep, debrisTint);
        }

        private void OnDestroy()
        {
            if (badge != null)
            {
                badge.Detach();
            }
        }
    }
}
