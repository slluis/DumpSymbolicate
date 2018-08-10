#!/bin/bash 

find /Applications/Visual\ Studio.app/ -iname '*.dll' -exec cp -- "{}" ./Assemblies/ \;
find /Applications/Visual\ Studio.app/ -iname '*.pdb' -exec cp -- "{}" ./Assemblies/ \;

