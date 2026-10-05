using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GInventory
{

    List<GameObject> items = new List<GameObject>();

    public void AddItem(GameObject i)
    {
        if (i == null || items.Contains(i))
            return;

        items.Add(i);

    }

    public GameObject FindItemWithTag(string tag)
    {

        foreach (GameObject i in items)
        {
            if (i != null && i.tag == tag)
            {
                return i;
            }
        }
        return null;
    }

    public void RemoveItem(GameObject i)
    {
        if (i == null)
            return;

        for (int indexToRemove = 0; indexToRemove < items.Count; indexToRemove++)
        {
            if (items[indexToRemove] == i)
            {
                items.RemoveAt(indexToRemove);
                break;
            }
        }
    }


}
