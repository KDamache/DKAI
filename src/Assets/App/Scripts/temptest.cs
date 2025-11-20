using UnityEngine;

public class temptest : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
    
    public void ReceivedTranscription(string txt)
    {
        Debug.Log($"Transcription received! : {txt}");
    }
}
