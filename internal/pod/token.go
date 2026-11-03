package pod

import "encoding/base64"

func base64Token(token string) string {
	return base64.StdEncoding.EncodeToString([]byte(token))
}
