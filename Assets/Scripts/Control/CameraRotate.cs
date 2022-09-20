using UnityEngine;

namespace MinecraftClient.Control
{
    public class CameraRotate : MonoBehaviour
    {
        public float rotateSpeed = 5F;

        void Update()
        {
            transform.eulerAngles = new(0F, transform.eulerAngles.y + Time.deltaTime * rotateSpeed , 0F);
        }
    }

}