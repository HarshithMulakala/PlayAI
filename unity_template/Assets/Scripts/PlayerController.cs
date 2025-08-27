using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float jumpForce = 7f;
    private Rigidbody2D _rb;
    private BoxCollider2D _collider;
    private bool _isGrounded;

    private void Awake()
    {
        _rb = gameObject.GetComponent<Rigidbody2D>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody2D>();
        _rb.gravityScale = 3f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        _collider = gameObject.GetComponent<BoxCollider2D>();
        if (_collider == null) _collider = gameObject.AddComponent<BoxCollider2D>();
    }

    private void Update()
    {
        float x = Input.GetAxisRaw("Horizontal");
        Vector2 v = _rb.velocity;
        v.x = x * moveSpeed;
        _rb.velocity = v;

        if (Input.GetButtonDown("Jump") && _isGrounded)
        {
            _rb.AddForce(Vector2.up * jumpForce, ForceMode2D.Impulse);
        }
    }

    private void OnCollisionEnter2D(Collision2D other)
    {
        foreach (var contact in other.contacts)
        {
            if (Vector2.Dot(contact.normal, Vector2.up) > 0.5f)
            {
                _isGrounded = true;
                break;
            }
        }
    }

    private void OnCollisionExit2D(Collision2D other)
    {
        _isGrounded = false;
    }
}


