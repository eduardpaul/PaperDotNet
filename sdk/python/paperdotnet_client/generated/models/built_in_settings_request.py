from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class BuiltInSettingsRequest(AdditionalDataHolder, Parsable):
    """
    Turns a built-in workflow on or off in the workspace; `parameters` (default: the ones it had) fill in its definition.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The enabled property
    enabled: Optional[bool] = None
    # The parameters property
    parameters: Optional[JsonObject] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> BuiltInSettingsRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: BuiltInSettingsRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return BuiltInSettingsRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "parameters": lambda n : setattr(self, 'parameters', n.get_object_value(JsonObject)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("enabled", self.enabled)
        writer.write_object_value("parameters", self.parameters)
        writer.write_additional_data_value(self.additional_data)
    

