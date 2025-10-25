using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class IsoEightDirController : MonoBehaviour
{
    [Header("Mouvement")]
    public float moveSpeed = 4f;

    Rigidbody2D rb;
    Animator anim;

    Vector2 input;       // entr�e normalis�e -1..1
    Vector2 lastMove = Vector2.down; // regarde bas par d�faut

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        anim = GetComponent<Animator>();
    }

    void Update()
    {
        // R�cup�re WASD/fl�ches (Input Manager classique)
        input.x = Input.GetAxisRaw("Horizontal");
        input.y = Input.GetAxisRaw("Vertical");
        input = input.normalized;
        Debug.Log("Input: " + input);
        // M�morise la derni�re direction non nulle pour l�Idle
        if (input.sqrMagnitude > 0.001f)
            lastMove = input;

        // Param�tres d'animation
        float speed = input.sqrMagnitude; // 0..1
        if (anim)
        {
            // En mouvement on m�lange vers la bonne direction
            anim.SetFloat("Speed", speed);
            Debug.Log("Speed: " + speed);

            if (speed > 0.001f)
            {
                anim.SetFloat("MoveX", input.x);
                anim.SetFloat("MoveY", input.y);
            }
            else
            {
                // � l�arr�t : regarde la derni�re direction
                anim.SetFloat("MoveX", lastMove.x);
                anim.SetFloat("MoveY", lastMove.y);
            }

            anim.SetFloat("LastMoveX", lastMove.x);
            anim.SetFloat("LastMoveY", lastMove.y);
        }
    }

    void FixedUpdate()
    {
        rb.linearVelocity = input * moveSpeed; // vitesse constante en diagonale
    }
}
