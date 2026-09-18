using UnityEngine;

namespace SparvagnRush.Gameplay
{
    public sealed class TramFollowCamera : MonoBehaviour
    {
        private Transform target;
        private TramController tram;
        private Camera cameraComponent;
        private Vector3 velocity;
        private readonly Vector3 offset = new(0f, 42f, -34f);

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            tram = newTarget.GetComponent<TramController>();
            cameraComponent = GetComponent<Camera>();
        }

        private void LateUpdate()
        {
            if (target == null) return;
            Vector3 desired = target.position + target.rotation * offset;
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref velocity, 0.18f);
            transform.LookAt(target.position + Vector3.up * 2f);
            if (cameraComponent != null && tram != null)
                cameraComponent.fieldOfView = Mathf.Lerp(cameraComponent.fieldOfView, 58f + Mathf.Abs(tram.Speed) * 0.35f, Time.deltaTime * 3f);
        }
    }
}
