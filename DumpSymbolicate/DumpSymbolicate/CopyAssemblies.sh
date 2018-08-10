#!/bin/bash 

find /Applications/Visual\ Studio.app/ -iname '*.dll' -exec cp -- "{}" ./Assemblies/ \;

