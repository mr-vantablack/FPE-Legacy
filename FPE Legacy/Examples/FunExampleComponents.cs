using System;
using FPE_Legacy.Content;
using FPE_Legacy.Networking;
using FPE_Legacy.Rpc;
using Il2CppInterop.Runtime.Attributes;
using MelonLoader;
using UnityEngine;

namespace FPE_Legacy.Examples
{
    [RegisterTypeInIl2Cpp]
    public class FunBreakable : MonoBehaviour, IFunContentReady
    {
        public float MaxHealth = 150;
        public bool DestroyOnDeath = true;
        public Vector3 DropOffset;
        public Rigidbody Body;
        [FunSerializable(OnChange = nameof(OnHealthChanged))]
        public float Health = 150;
        public FunBreakable(IntPtr pointer) : base(pointer) { }
        [HideFromIl2Cpp]
        public void OnContentReady()
        {
            MelonLogger.Msg("[FunContent] Breakable ready: " + gameObject.name);
        }
        [HideFromIl2Cpp]
        public void ApplyDamage(float amount)
        {
            if (!FunNetwork.IsServer || !float.IsFinite(amount) || amount <= 0) return;
            Health = Mathf.Max(0, Health - amount);
            if (Health <= 0 && DestroyOnDeath) FunNetwork.GetIdentity(this)?.Despawn();
        }
        private void OnHealthChanged(float oldValue, float newValue) => MelonLogger.Msg($"[FunContent] Health: {oldValue} -> {newValue}");
    }
    [RegisterTypeInIl2Cpp]
    public class FunSpinner : MonoBehaviour
    {
        public Vector3 DegreesPerSecond = new Vector3(0, 45, 0);
        public FunSpinner(IntPtr pointer) : base(pointer) { }
        public void Update()
        {
            if (FunNetwork.IsServer) transform.Rotate(DegreesPerSecond * Time.deltaTime, Space.Self);
        }
    }

    [RegisterTypeInIl2Cpp]
    public class FunRandomColor : MonoBehaviour
    {
        public FunRandomColor(IntPtr pointer) : base(pointer) { }
        private Material objMaterial;
        public FunNetworkIdentity identity;

        public Color color1 = Color.black;
        public Color color2 = Color.white;
        [FunSerializable(WritePermission = FunSerializableWritePermission.Owner)]
        public Color curColor = Color.blue;

        public float speed = 2.0f;

        public void Start()
        {
            identity =FunNetwork.GetIdentity(this);
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                objMaterial = renderer.material;
            }
        }

        public void Update()
        {
            if (objMaterial == null) return;

            if (identity.IsOwner)
            {
                float t = (Mathf.Sin(Time.time * speed) + 1f) / 2f;
                curColor = Color.Lerp(color1, color2, t);
            } 
            objMaterial.color = curColor;
        }
    }
}
