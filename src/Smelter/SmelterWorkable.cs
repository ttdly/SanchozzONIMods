﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Smelter
{
    // старая анимация смещена. придется вернуть обработку смещения
    public class SmelterWorkable : ComplexFabricatorWorkable
    {
        public override Vector3 GetWorkOffset()
        {
            return AnimOffset;
        }
    }
}
