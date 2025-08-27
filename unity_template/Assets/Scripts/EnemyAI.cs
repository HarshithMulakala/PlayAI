using UnityEngine;

public class EnemyAI : MonoBehaviour
{
    public float patrolAmplitude = 2f;
    public float patrolSpeed = 1f;
    private Vector3 _origin;

    private void Start()
    {
        _origin = transform.position;
        var rb = gameObject.GetComponent<Rigidbody2D>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody2D>();
        rb.gravityScale = 0.5f;
        rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        var col = gameObject.GetComponent<Collider2D>();
        if (col == null) col = gameObject.AddComponent<BoxCollider2D>();
    }

    private void Update()
    {
        float dx = Mathf.Sin(Time.time * patrolSpeed) * patrolAmplitude;
        transform.position = new Vector3(_origin.x + dx, transform.position.y, transform.position.z);
    }
}


